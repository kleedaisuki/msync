using Msync.Core;
using Msync.Core.Native;

internal static class StorageCopyChecks
{
    internal static int Run()
    {
        NativeType number = NativeType.Of(NativeKind.Number);
        NativeAllocation target = new(NativeLayout.Repeat(number, 1));
        target.Field(0).Store(NativeValue.FromNumber(17));
        NativeValue[] values = [NativeValue.Null, NativeValue.True,
            NativeValue.FromNumber(42), NativeValue.FromString(new string('x', 64)),
            NativeValue.FromAddress(target.Field(0)),
            NativeValue.FromPath(new("a/b", "/origin", new(ConnectionKind.Ssh, "host"), "/home", "captured"))];
        NativeType union = NativeType.Union(values.Select(value => NativeType.Of(value.Kind)).ToArray());
        int checks = 0;
        CheckOwnerRebinding(number);
        checks++;
        NativeAllocation overlap = new(NativeLayout.Repeat(union, 1));
        NativeAddress boolean = new(overlap, new(union.Representation.PayloadOffset, 0), NativeType.Of(NativeKind.Bool));
        NativeStorage.StoreTrusted(boolean.Type, boolean, NativeValue.True);
        NativeStorage.CopyKindTrusted(NativeKind.Bool, boolean, overlap.Field(0));
        if (overlap.Field(0).Load() != NativeValue.True)
            throw new InvalidOperationException("Carrier widening erased an overlapping scalar payload");
        checks++;
        NativeAllocation reused = new(NativeLayout.Repeat(union, 1));
        foreach (NativeValue previous in values)
            foreach (NativeValue next in values)
            {
                reused.Field(0).Store(previous);
                reused.Field(0).Store(next);
                if (reused.Field(0).Load() != next)
                    throw new InvalidOperationException("Union overwrite retained stale payload bits or roots");
                checks++;
            }
        foreach (NativeValue value in values)
        {
            CheckClear(value, union);
            checks++;
            foreach ((int source, int destination) in new[] { (0, 0), (0, 1), (1, 0) })
            {
                CheckCopy(value, union, source, destination, false);
                CheckCopy(value, union, source, destination, true);
                checks += 2;
            }
        }
        NativeAllocation empty = new(NativeLayout.Repeat(number, 2));
        try { NativeStorage.Copy(empty.Field(0), empty.Field(1)); }
        catch (InvalidOperationException)
        {
            try { _ = empty.Field(0).Load(); }
            catch (InvalidOperationException) { return checks + 2; }
            throw new InvalidOperationException("The public storage read accepted an uninitialized source");
        }
        throw new InvalidOperationException("The public storage copy accepted an uninitialized source");
    }

    private static void CheckCopy(NativeValue value, NativeType union, int sourceRoot, int destinationRoot, bool narrow)
    {
        NativeType type = NativeType.Of(value.Kind);
        NativeAllocation buffer = new(NativeLayout.Repeat(union, 4));
        NativeAddress source = new(buffer, new(0, sourceRoot), narrow ? union : type);
        NativeAddress destination = new(buffer, new(union.Representation.Bytes, destinationRoot), type);
        NativeStorage.StoreTrusted(source.Type, source, value);
        if (narrow) NativeStorage.CopyKindTrusted(value.Kind, source, destination);
        else NativeStorage.CopyTrusted(type, source, destination);
        GC.Collect();
        if (NativeStorage.LoadTrusted(type, destination) != value)
            throw new InvalidOperationException($"{value.Kind}: overlapping reference copy lost its payload");
        if (value.Kind == NativeKind.Address && NativeStorage.LoadTrusted(type, destination).AsAddress().Type != value.AsAddress().Type)
            throw new InvalidOperationException("An address copy lost its physical pointee type");
    }

    private static void CheckClear(NativeValue value, NativeType union)
    {
        NativeAllocation buffer = new(NativeLayout.Repeat(union, 2));
        NativeAddress cell = buffer.Field(0), neighbor = buffer.Field(1);
        cell.Store(value);
        neighbor.Store(value);
        cell.Clear();
        if (cell.IsInitialized || neighbor.Load() != value || buffer.InitializedValues.Count() != 1)
            throw new InvalidOperationException("Clearing a cell changed its neighbor or left a readable value");
        cell.Store(NativeValue.FromNumber(7));
        if (cell.Load() != NativeValue.FromNumber(7))
            throw new InvalidOperationException("Reusing cleared storage retained the previous representation");
    }

    private static void CheckOwnerRebinding(NativeType number)
    {
        NativeLayout layout = new(number);
        NativeAllocation owner = new(layout, allocate: false);
        NativeAddress address = owner.Field(0);
        ref NativeStorage.Data data = ref owner.Storage;
        byte[] first = new byte[layout.RawSize * 2], second = new byte[layout.RawSize];
        data.BindStack(first, [], new bool[first.Length], new(layout.RawSize, 0));
        address.Store(NativeValue.FromNumber(13));
        IEnumerable<NativeValue> values = owner.InitializedValues;
        data.DetachStack();
        data.BindStack(second, [], new bool[second.Length], default);
        address.Store(NativeValue.FromNumber(29));
        if (!ReferenceEquals(owner.Storage.RawBytes, second) || address.Load().AsNumber() != 29
            || values.Single().AsNumber() != 29 || BitConverter.ToDouble(first, layout.RawSize) != 13)
            throw new InvalidOperationException("Storage rebinding copied its state instead of updating the allocation owner");
    }
}
