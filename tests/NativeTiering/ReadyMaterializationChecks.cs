using Msync.Compiler;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class ReadyMaterializationChecks
{
    internal static unsafe int Run()
    {
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile("let n=1; n+1", "materialization.msync", 0));
        NativeImage image = TierFixture.Link(program);
        using NativeExecutionFunction function = new(image, program.EntryPoint, new(NativeJitPolicy.Force));
        using NativeCompiledFunction version = NativeCompiledFunction.Compile(image, function, NativeJitPolicy.Force)!;
        int slot = version.Layout.FixedSlots.First(entry => function.IsPrivateTemporary(entry.Slot)
            && function.HasKind(entry.Slot, NativeKind.Number)).Slot;
        NativeMemory memory = new();
        NativeFrame frame = new(image, program.EntryPoint, null, [], memory);
        using NativeTierMemory native = new(frame, memory, function, 0, version);

        frame.Set(slot, NativeValue.FromNumber(42));
        NativeAddress proven = frame.Location(slot);
        if (!frame.IsReady(slot, proven) || frame.GetDatum(slot).Value.AsNumber() != 42)
            throw new InvalidOperationException("A ready private scalar lost its payload");

        foreach (string state in new[] { "pending", "success", "fault", "cancelled" })
        {
            TaskCompletionSource<NativeValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            frame.SetDatum(slot, new(new NativeFuture(completion.Task, logicalType: Msync.Core.TypeRef.Number)));
            if (state == "success") completion.SetResult(NativeValue.FromNumber(42));
            if (state == "fault") completion.SetException(new InvalidOperationException("unused"));
            if (state == "cancelled") completion.SetCanceled();
            native.Reset(frame, memory, function, version, null, 0, 0);
            bool pending = memory.IsPending(frame.Location(slot));
            if (frame.IsReady(slot, frame.Location(slot)) == pending)
                throw new InvalidOperationException($"Initialization proof bypassed Future readiness: {state}");
            if (pending != (state != "success"))
                throw new InvalidOperationException($"Native setup changed the wrong Future state: {state}");
            if (state == "success" && frame.GetDatum(slot).Value.AsNumber() != 42)
                throw new InvalidOperationException("Successful materialization lost the scalar value");
            if (state == "fault") _ = completion.Task.Exception;
            memory.ResetFrame(frame.Allocation);
        }

        NativeProgram borrowed = NativePreparation.Prepare(Compiler.Compile("fn identity(n: Number) { n }; identity(1)", "borrowed-materialization.msync", 0));
        int target = Enumerable.Range(0, borrowed.Functions.Count).First(index => borrowed.Functions[index].ParameterCount == 1);
        NativeImage borrowedImage = TierFixture.Link(borrowed);
        using NativeExecutionFunction borrowedFunction = new(borrowedImage, target, new(NativeJitPolicy.Force));
        using NativeCompiledFunction borrowedVersion = NativeCompiledFunction.Compile(borrowedImage, borrowedFunction, NativeJitPolicy.Force)!;
        int parameter = borrowed.Functions[target].CaptureCount;
        if (borrowedVersion.Layout.TryFixed(parameter, out _))
            throw new InvalidOperationException("Fixture lost its borrowed parameter ABI");
        NativeMemory borrowedMemory = new();
        NativeFrame borrowedFrame = new(borrowedImage, target, null, [new NativeDatum(NativeValue.FromNumber(0))], borrowedMemory);
        TaskCompletionSource<NativeValue> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        borrowedFrame.SetDatum(parameter, new(new NativeFuture(ready.Task, logicalType: Msync.Core.TypeRef.Number)));
        ready.SetResult(NativeValue.FromNumber(7));
        using NativeTierMemory borrowedNative = new(borrowedFrame, borrowedMemory, borrowedFunction, 0, borrowedVersion);
        if (!borrowedMemory.IsPending(borrowedFrame.Location(parameter)))
            throw new InvalidOperationException("Native setup wrote through an externally bindable parameter");
        return 6;
    }
}
