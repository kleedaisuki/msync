using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class FrameBindingChecks
{
    internal static int Run()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 1)],
            [new("bindings", [new(0, NativeSlotRole.Local), new(0), new(1)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 0),
                 new(NativeOp.FrameAddress, 2, 1), new(NativeOp.BindLocation, A: 0, B: 2),
                 new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        program = TierFixture.Plan(program);
        NativeArtifact.Validate(program);
        NativeImage image = new(program, new());
        if (!image.Frame(0).BindingSlots.SequenceEqual([0]))
            throw new Exception("Immutable frame locations acquired mutable binding state.");
        NativeMemory memory = new();
        NativeFrame frame = new(image, 0, null, [], memory);
        frame.Set(0, NativeValue.FromNumber(7));
        frame.Set(1, NativeValue.FromNumber(13));
        NativeAddress old = frame.Location(0);
        NativeAddress target = frame.Location(1);
        frame.Bind(0, target);
        if (frame.Location(0) != target || old == target || old.Load().AsNumber() != 7)
            throw new Exception("Binding redirected a previously obtained frame address.");
        frame.Set(0, NativeValue.FromNumber(21));
        if (frame.GetDatum(1).Value.AsNumber() != 21 || old.Load().AsNumber() != 7)
            throw new Exception("A compact frame binding lost its canonical alias.");
        frame.SetPointer(2, old);
        if (frame.Pointer(2) != old)
            throw new Exception("A static pointer cell lost an old pre-bind address.");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (frame.Pointer(2).Load().AsNumber() != 7)
            throw new Exception("A compact binding failed to keep an escaping frame owner alive.");

        NativeProgram persistent = program with { PreserveLocals = true, SessionRoots = [1] };
        NativeImage sessionImage = new(persistent, new());
        if (!sessionImage.Frame(0).BindingSlots.SequenceEqual([0, 1]))
            throw new Exception("Persistent temporary roots were omitted from mutable bindings.");
        Dictionary<int, NativeAddress> locations = [];
        NativeFrame first = new(sessionImage, 0, null, [], memory, locations);
        first.Set(0, NativeValue.FromNumber(31));
        first.Set(1, NativeValue.FromNumber(37));
        NativeAddress heap = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
        heap.Store(NativeValue.FromNumber(41));
        first.Bind(0, heap);
        NativeFrame second = new(sessionImage, 0, null, [], memory, locations);
        if (second.Location(0) != heap || second.GetDatum(0).Value.AsNumber() != 41 || second.GetDatum(1).Value.AsNumber() != 37)
            throw new Exception("Session frame construction forgot persisted bindings or temporary roots.");
        NativeFrame ephemeral = new(sessionImage, 0, null, [], memory);
        ephemeral.Set(0, NativeValue.FromNumber(43));
        if (!ReferenceEquals(ephemeral.Location(0).Allocation, ephemeral.Location(1).Allocation)
            || first.GetDatum(0).Value.AsNumber() != 41 || first.GetDatum(1).Value.AsNumber() != 37)
            throw new Exception("A preparation frame acquired session-owned storage without an owner.");
        ephemeral.Bind(0, heap);
        if (ephemeral.Location(0) != heap)
            throw new Exception("An ephemeral session-capable frame cannot rebind without a persistence owner.");
        return 9;
    }
}
