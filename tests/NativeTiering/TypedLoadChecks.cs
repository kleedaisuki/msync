using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class TypedLoadChecks
{
    internal static async Task<int> RunAsync()
    {
        foreach ((TypeKind type, NativeValue value) in new[]
        {
            (TypeKind.Number, NativeValue.FromNumber(-19.25)),
            (TypeKind.Bool, NativeValue.FromBool(true))
        })
        {
            NativeProgram program = new([], [new("typed-load", [new(0, NativeSlotRole.Parameter), new(1), new(0)],
                [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0), new(NativeOp.Return, A: 2)],
                new([0], 0, []), ParameterCount: 1)], [new(type), new(TypeKind.Never, Pointee: 0)], []);
            program = TierFixture.Plan(program);
            NativeArtifact.Validate(program);
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
            NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
            if (actual != value || interpreter.TierStatistics.RawOperations != 2)
                throw new InvalidOperationException("A typed primitive Load did not execute directly against its actual pointee.");
        }
        await PendingSourceAsync();
        await ReboundLocationAsync();
        await UnionSourceAsync();
        await MutatedPointerCarrierAsync();
        await ChangedPointeeAsync();
        await SynchronizedSourceAsync();
        return 10;
    }

    private static async Task SynchronizedSourceAsync()
    {
        SourceSpan loadSpan = new("typed-load-resume.msync", 42, 3);
        NativeProgram program = new([new(NativeKind.Number, Number: 0)],
        [
            new("reader", [new(1, NativeSlotRole.Parameter), new(1), new(0), new(2), new(0), new(4)],
                [new(NativeOp.Copy, 1, 0), new(NativeOp.Load, 2, 1, Type: 0),
                 new(NativeOp.CallNative, 3, Symbol: "io.read_line", Inputs: []),
                 new(NativeOp.Unary, 5, 3, Immediate: (long)NativeUnary.IsNull),
                 new(NativeOp.Branch, A: 5, B: 5, C: 6), new(NativeOp.Unreachable, Symbol: "Unexpected EOF"),
                 new(NativeOp.Load, 4, 1, Type: 0, Source: loadSpan), new(NativeOp.Return, A: 4)],
                new([1], 0, []), ParameterCount: 1),
            new("ready-writer", [new(1, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter)],
                [new(NativeOp.Store, A: 0, B: 1, C: 0, Type: 0), new(NativeOp.Return, A: 1)],
                new([1, 0], 0, []), ParameterCount: 2),
            new("pending-writer", [new(1, NativeSlotRole.Parameter), new(3, NativeSlotRole.Parameter), new(0), new(0)],
                [new(NativeOp.CallNative, 2, Symbol: "process.exit_code", Inputs: [1]),
                 new(NativeOp.Store, A: 0, B: 2, C: 0, Type: 0), new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)],
                new([1, 3], 0, []), ParameterCount: 2),
            new("clear-writer", [new(1, NativeSlotRole.Parameter), new(0), new(0)],
                [new(NativeOp.BindLocation, A: 1, B: 0), new(NativeOp.Drop, A: 1),
                 new(NativeOp.Constant, 2, 0), new(NativeOp.Return, A: 2)],
                new([1], 0, []), ParameterCount: 1)
        ], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.String | TypeKind.Null), new(TypeKind.Process), new(TypeKind.Bool)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        foreach (int writer in new[] { 1, 2, 3 })
        {
            GateReader gate = new();
            NativeAddress address = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
            address.Store(NativeValue.FromNumber(1));
            NativeValue pointer = NativeValue.FromAddress(address);
            TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using NativeInterpreter interpreter = new(program, new() { Input = gate, Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
            Task<NativeValue> reader = interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [pointer]).AsTask();
            try
            {
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (reader.IsCompleted) throw new InvalidOperationException("The synchronized reader did not wait for input.");
                interpreter.UpdateProgram(program with { Constants = [new(NativeKind.Number, Number: 999)] });
                NativeValue[] arguments = writer switch
                {
                    1 => [pointer, NativeValue.FromNumber(31)],
                    2 => [pointer, NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })],
                    _ => [pointer]
                };
                await interpreter.InvokeAsync(NativeValue.FromFunction(new(writer)), arguments);
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                gate.Release.SetResult("resume");
                if (writer == 2)
                    completion.SetResult(new(31, "", ""));
                if (writer == 3)
                {
                    try { await reader.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("A cleared source remained readable."); }
                    catch (MsyncException error) when (error.Span == loadSpan) { }
                    if (!reader.IsFaulted) throw new InvalidOperationException("A cleared source did not preserve its checked-load failure.");
                }
                else if ((await reader.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() != 31)
                    throw new InvalidOperationException("A synchronized helper mutation was not visible after resumption.");
                if (gate.Reads != 1)
                    throw new InvalidOperationException("A typed Load replayed its preceding gated effect.");
            }
            finally
            {
                gate.Release.TrySetResult("resume");
                completion.TrySetResult(new(31, "", ""));
            }
        }
    }

    private static async Task ChangedPointeeAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 9)],
            [new("changed-pointee", [new(0, NativeSlotRole.Parameter), new(1), new(0), new(0), new(0)],
                [new(NativeOp.Constant, 3, 0), new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0),
                 new(NativeOp.Store, A: 1, B: 3, C: 0, Type: 0), new(NativeOp.Load, 4, 1, Type: 0), new(NativeOp.Return, A: 4)],
                new([0], 0, []), ParameterCount: 1)], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(1)]);
        if (result.AsNumber() != 9)
            throw new InvalidOperationException("A cached typed address hid a helper's updated primitive contents.");
    }

    private static async Task MutatedPointerCarrierAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 9)],
            [new("pointer-store", [new(0, NativeSlotRole.Parameter), new(0), new(1), new(2), new(1), new(0)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.FrameAddress, 2, 0), new(NativeOp.FrameAddress, 3, 2),
                 new(NativeOp.FrameAddress, 4, 1), new(NativeOp.Store, A: 3, B: 4, C: 0, Type: 1),
                 new(NativeOp.Load, 5, 2, Type: 0), new(NativeOp.Return, A: 5)],
                new([0], 0, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Never, Pointee: 1)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        NativeExecutionFunction executable = new(TierFixture.Link(program), 0, new(NativeJitPolicy.Force));
        if (executable.RawWritable[2]) throw new InvalidOperationException("An indirectly mutable pointer carrier retained a native pointee cache.");
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(1)]);
        if (result.AsNumber() != 9) throw new InvalidOperationException("An indirect pointer store was hidden by a stale descriptor.");
    }

    private static async Task UnionSourceAsync()
    {
        NativeProgram program = new([], [new("union-pointee", [new(0, NativeSlotRole.Parameter), new(1), new(0)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0), new(NativeOp.Return, A: 2)],
            new([0], 0, []), ParameterCount: 1)], [new(TypeKind.Number | TypeKind.String), new(TypeKind.Never, Pointee: 0)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 2, 2, 2) });
        for (int invocation = 0; invocation < 2; invocation++)
            if ((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(11)])).AsNumber() != 11)
                throw new InvalidOperationException("Union primitive Load changed its value.");
        if (interpreter.TierStatistics.Entries == 0)
            throw new InvalidOperationException("Observed union Load did not execute its native physical-kind path.");
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        NativeValue changed = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromString("changed𐀀")]);
        if (changed.AsString() != "changed𐀀" || interpreter.TierStatistics.GuardMisses != 0)
            throw new InvalidOperationException("A complete union transfer invented an unnecessary value-kind assumption.");
    }

    private static async Task PendingSourceAsync()
    {
        NativeProgram program = new([], [new("pending-pointee",
            [new(0, NativeSlotRole.Parameter), new(1), new(2), new(1)],
            [new(NativeOp.FrameAddress, 2, 1), new(NativeOp.CallNative, 1, Symbol: "process.exit_code", Inputs: [0]),
             new(NativeOp.Load, 3, 2, Type: 1), new(NativeOp.Return, A: 3)], new([0], 1, []), ParameterCount: 1)],
            [new(TypeKind.Process), new(TypeKind.Number), new(TypeKind.Never, Pointee: 1)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        Task<NativeValue> pending = interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask();
        try
        {
            if (pending.IsCompleted)
                throw new InvalidOperationException("A newly pending pointee was read as stale primitive bytes.");
            completion.SetResult(new(23, "", ""));
            NativeValue result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (result.AsNumber() != 23) throw new InvalidOperationException("Pending Load did not preserve its original continuation.");
        }
        finally { completion.TrySetResult(new(23, "", "")); }
    }

    private static async Task ReboundLocationAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 7), new(NativeKind.Number, Number: 9)],
            [new("retained-pointer", [new(1, NativeSlotRole.Parameter), new(0), new(1), new(0)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.FrameAddress, 2, 1), new(NativeOp.BindLocation, A: 1, B: 0),
                 new(NativeOp.Constant, 1, 1), new(NativeOp.Load, 3, 2, Type: 0), new(NativeOp.Return, A: 3)],
                new([1], 0, []), ParameterCount: 1)], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        NativeAddress external = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
        external.Store(NativeValue.FromNumber(1));
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(external)]);
        if (result.AsNumber() != 7 || external.Load().AsNumber() != 9)
            throw new InvalidOperationException("A retained typed pointer followed a later frame-location rebind.");
    }
}
