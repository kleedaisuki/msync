using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class RawWriteChecks
{
    internal static async Task<int> RunAsync()
    {
        int pendingChecks = PendingFlags();
        NativeCodeType[] types = [new(TypeKind.Process), new(TypeKind.Number), new(TypeKind.Never, Pointee: 1)];
        NativeProgram geometry = new([new(NativeKind.Number, Number: 0), new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 9)],
            [new("geometric-frame", [new(0, NativeSlotRole.Parameter), new(1), new(1), new(2), new(1), new(2), new(1)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Constant, 2, 0), new(NativeOp.FrameAddress, 3, 1),
                 new(NativeOp.Constant, 4, 1), new(NativeOp.IndexAddress, 5, 3, 4, 0),
                 new(NativeOp.CallNative, 6, Symbol: "process.exit_code", Inputs: [0]),
                 new(NativeOp.Store, A: 5, B: 6, C: 0, Type: 1), new(NativeOp.Constant, 2, 2), new(NativeOp.Return, A: 2)],
                new([0], 1, []), ParameterCount: 1)], types, [new([new("", 1)])]);
        try { NativeArtifact.Validate(geometry); throw new InvalidOperationException("A typed field pointer was accepted as an indexed namespace region."); }
        catch (MsyncException) { }
        NativeExecutionFunction function = new(new(geometry, new()), 0, new(NativeJitPolicy.Force));
        if (function.RawWritable.Any(value => value))
            throw new InvalidOperationException("Geometric frame access was mistaken for an unexposed temporary.");
        NativeFunctionCode original = geometry.Functions[0];
        NativeInstruction[] directCode = original.Instructions.Select(instruction => instruction.Op switch
        {
            NativeOp.IndexAddress => instruction with { Op = NativeOp.Copy, A = 3, B = -1, C = -1 },
            NativeOp.Constant when instruction.Result == 2 && instruction.A == 2 => instruction with { Result = 1 },
            NativeOp.Return => instruction with { A = 1 },
            _ => instruction
        }).ToArray();
        NativeProgram exposed = geometry with { Functions = [original with { Instructions = directCode }] };
        NativeArtifact.Validate(exposed);
        NativeExecutionFunction exposedFunction = new(new(exposed, new()), 0, new(NativeJitPolicy.Force));
        if (exposedFunction.RawWritable[1]) throw new InvalidOperationException("An address-exposed temporary was eligible for raw overwrite.");
        TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NativeInterpreter interpreter = new(exposed, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        try
        {
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (result.AsNumber() != 9) throw new InvalidOperationException("An indexed pending destination bypassed metadata cleanup.");
        }
        finally { completion.TrySetResult(new(7, "", "")); }
        NativeProgram escaped = new([], [new("escape", [new(0), new(1)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Return, A: 1)], new([], 1, []))],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeArtifact.Validate(escaped);
        NativeExecutionFunction escape = new(new(escaped, new()), 0, new(NativeJitPolicy.Force));
        if (escape.RawWritable.Any(value => value))
            throw new InvalidOperationException("Escaping frame locations were eligible for unguarded pending metadata assumptions.");
        return 6 + pendingChecks;
    }

    private static unsafe int PendingFlags()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 1)],
            [new("pending-flags", [new(0), new(0), new(0)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.Number)], []);
        NativeImage image = new(program, new());
        NativeMemory memory = new();
        NativeFrame frame = new(image, 0, null, [], memory);
        NativeExecutionFunction function = new(image, 0, new(NativeJitPolicy.Force));
        using NativeTierMemory native = new(frame, memory, function, 0);
        NativeDatum pending = new(new NativeFuture(new TaskCompletionSource<NativeValue>().Task,
            logicalType: TypeRef.Number));
        void Check(int slot, uint flags)
        {
            if (native.Context->Slots[slot].Flags != flags)
                throw new InvalidOperationException($"Slot {slot} has stale native pending/ownership flags.");
        }
        Check(0, 0);
        memory.Write(frame.Location(0), pending);
        native.RefreshPending(1);
        Check(0, 2);
        memory.Copy(frame.Location(0), frame.Location(2));
        native.RefreshPending(1);
        Check(2, 2);
        frame.Set(0, NativeValue.FromNumber(7));
        native.RefreshPending(0);
        Check(0, 0);
        memory.Clear(frame.Location(2));
        native.RefreshPending(2);
        Check(2, 0);
        NativeProgram bound = new([new(NativeKind.Number, Number: 1)], [new("bound-flags", [new(0), new(0), new(0), new(1)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.FrameAddress, 3, 0), new(NativeOp.BindLocation, A: 1, B: 3),
             new(NativeOp.BindLocation, A: 2, B: 3), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Null)], []);
        NativeArtifact.Validate(bound);
        NativeImage boundImage = new(bound, new());
        NativeFrame boundFrame = new(boundImage, 0, null, [], memory);
        NativeExecutionFunction boundFunction = new(boundImage, 0, new(NativeJitPolicy.Force));
        using NativeTierMemory boundNative = new(boundFrame, memory, boundFunction, 0);
        void CheckBound(int slot, uint flags)
        {
            if (boundNative.Context->Slots[slot].Flags != flags)
                throw new InvalidOperationException($"Bound slot {slot} has stale native pending/ownership flags.");
        }
        NativeAddress external = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
        boundFrame.Bind(1, external);
        boundNative.Refresh(1);
        CheckBound(1, 0);
        memory.Write(external, pending);
        boundNative.RefreshPending(1);
        CheckBound(1, 2);
        boundFrame.Bind(2, boundFrame.Location(0));
        boundNative.Refresh(2);
        memory.Write(boundFrame.Location(0), pending);
        boundNative.RefreshPending(1);
        CheckBound(0, 2);
        CheckBound(2, 2);
        boundFrame.Set(0, NativeValue.FromNumber(9));
        boundNative.RefreshPending(0);
        CheckBound(0, 0);
        // An alias may conservatively retain true until it is itself refreshed.
        CheckBound(2, 2);
        boundNative.RefreshPending(2);
        CheckBound(2, 0);
        return 12;
    }
}
