using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class NarrowChecks
{
    internal static async Task<int> RunAsync()
    {
        SourceSpan span = new("checked-native-narrow.msync", 17, 5);
        foreach ((TypeKind kind, NativeValue value) in new[]
        {
            (TypeKind.Number, NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(0xfff8000000001234))),
            (TypeKind.Number, NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(0x7ff0000000000001))),
            (TypeKind.Number, NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(0x8000000000000000))),
            (TypeKind.Bool, NativeValue.FromBool(true)),
            (TypeKind.Bool, NativeValue.FromBool(false))
        })
        {
            NativeProgram program = new([], [new("checked-narrow", [new(0, NativeSlotRole.Parameter), new(1)],
                [new(NativeOp.Narrow, 1, 0, Type: 1, Source: span), new(NativeOp.Return, A: 1)],
                new([0], 1, []), ParameterCount: 1)], [new(kind | TypeKind.String), new(kind)], []);
            NativeArtifact.Validate(program);
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
            NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
            if (actual != value || interpreter.TierStatistics.RawOperations != 1)
                throw new InvalidOperationException("A ready scalar refinement changed its representation or did not execute natively.");
            try
            {
                await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromString("wrong-kind")]);
                throw new InvalidOperationException("A native refinement accepted a different union alternative.");
            }
            catch (MsyncException error) when (error.Span == span) { }
            if (interpreter.TierStatistics.GuardMisses != 1)
                throw new InvalidOperationException("Failed refinement did not exit at the original Narrow.");
        }
        await PendingAsync();
        return 6;
    }

    private static async Task PendingAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 9)],
            [new("pending-narrow", [new(0, NativeSlotRole.Parameter), new(1), new(2), new(2)],
                [new(NativeOp.CallNative, 1, Symbol: "process.exit_code", Inputs: [0]), new(NativeOp.Narrow, 2, 1, Type: 2),
                 new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)], new([0], 2, []), ParameterCount: 1)],
            [new(TypeKind.Process), new(TypeKind.Number | TypeKind.String), new(TypeKind.Number)], []);
        NativeArtifact.Validate(program);
        TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        try
        {
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (result.AsNumber() != 9 || completion.Task.IsCompleted || interpreter.TierStatistics.GuardMisses != 1)
                throw new InvalidOperationException("A native checked refinement forced a discarded future.");
        }
        finally { completion.TrySetResult(new(7, "", "")); }
    }
}
