using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly struct NativeArguments
{
    private readonly IReadOnlyList<NativeDatum>? values;
    private readonly NativeFrame? frame;
    private readonly NativeAllocation? storage;
    private readonly IReadOnlyList<int>? slots;
    private readonly bool[]? borrowed;

    internal NativeArguments(IReadOnlyList<NativeDatum> values, bool snapshot = false) =>
        (this.values, frame, storage, slots, borrowed, IsSnapshot, BorrowedCount) = (values, null, null, null, null, snapshot, 0);
    internal NativeArguments(NativeFrame frame, IReadOnlyList<int> slots, bool[]? borrowed, int borrowedCount) =>
        (values, this.frame, storage, this.slots, this.borrowed, IsSnapshot, BorrowedCount) = (null, frame, null, slots, borrowed, false, borrowedCount);
    internal NativeArguments(NativeAllocation storage, bool[] borrowed, int borrowedCount) =>
        (values, frame, this.storage, slots, this.borrowed, IsSnapshot, BorrowedCount) = (null, null, storage, null, borrowed, false, borrowedCount);
    internal int Count => storage?.Layout.Count ?? slots?.Count ?? values?.Count ?? 0;
    internal bool IsFrameView => frame is not null && Count != 0;
    internal bool IsSnapshot { get; }
    internal int BorrowedCount { get; }
    internal NativeDatum Read(int index, NativeMemory memory) => values is not null ? values[index]
        : memory.Read(Location(index));
    private NativeAddress Location(int index) => frame is not null ? frame.Location(slots![index]) : storage!.Field(index);

    internal void Initialize(NativeFrame destination, int parameter)
    {
        int target = destination.CaptureCount + parameter;
        if (values is not null) { destination.SetDatum(target, values[parameter]); return; }
        NativeAddress source = Location(parameter);
        if (borrowed?[parameter] == true) destination.BorrowParameter(target, source);
        else destination.CopyArgument(source, target);
    }

    internal void CopyTo(NativeAllocation destination, NativeMemory memory)
    {
        for (int index = 0; index < Count; index++)
        {
            NativeAddress target = destination.Field(index);
            if (values is not null) memory.Write(target, values[index]);
            else memory.Copy(Location(index), target);
        }
    }

    internal IReadOnlyList<NativeDatum> Materialize(NativeMemory memory)
    {
        if (values is not null) return values;
        if (Count == 0) return [];
        NativeDatum[] snapshot = new NativeDatum[Count];
        for (int index = 0; index < snapshot.Length; index++) snapshot[index] = Read(index, memory);
        return snapshot;
    }
}

internal sealed class NativeCallSite(NativeImage image, NativeFunctionCode caller, NativeInstruction call, bool[] privateSlots)
{
    private readonly IReadOnlyList<int> inputs = call.Inputs ?? [];
    private readonly bool stable = (call.Inputs ?? []).All(slot => privateSlots[slot]);
    private readonly (bool[]? Mask, int Count) borrowing = Borrowed(image, caller, call);

    internal NativeArguments Arguments(NativeFrame frame)
    {
        if (stable) return new(frame, inputs, borrowing.Mask, borrowing.Count);
        NativeDatum[] snapshot = new NativeDatum[inputs.Count];
        for (int index = 0; index < snapshot.Length; index++) snapshot[index] = frame.GetDatum(inputs[index]);
        return new(snapshot, snapshot: true);
    }

    private static (bool[]? Mask, int Count) Borrowed(NativeImage image, NativeFunctionCode caller, NativeInstruction call)
    {
        if (call.B < 0 || call.C >= 0) return (null, 0);
        NativeFunctionCode callee = image.Program.Functions[call.B];
        FrameAllocation target = image.Frame(call.B);
        IReadOnlyList<int> inputs = call.Inputs ?? [];
        bool[] mask = inputs.Select((slot, index) => target.ParameterBorrows[index]
            && image.Type(caller.Slots[slot].Type).Equals(image.Type(callee.Slots[callee.CaptureCount + index].Type))).ToArray();
        return (mask, mask.Count(value => value));
    }
}
