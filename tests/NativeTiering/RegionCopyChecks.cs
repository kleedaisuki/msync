using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class RegionCopyChecks
{
    internal static int Run()
    {
        NativeValue[] values = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(-0d),
            NativeValue.FromString("layout-𐀀"), NativeValue.FromPath(new("./file", "/origin")),
            NativeValue.FromConnection(new(ConnectionKind.Ssh, "configured-alias")),
            NativeValue.FromFunction(new(-1, NativeName: "math.abs")),
            NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(7)], TypeRef.Number)),
            NativeValue.FromProcess(new(0, "out", "err")), NativeValue.FromType(TypeRef.Number),
            NativeValue.FromAddress(new NativeAllocation(new NativeLayout()).Address)];
        foreach (NativeValue value in values)
        foreach (bool union in new[] { false, true })
        {
            NativeType type = NativeType.Of(value.Kind);
            if (union) type = NativeType.Union(type, NativeType.Of(value.Kind == NativeKind.Null ? NativeKind.Number : NativeKind.Null));
            NativeLayout layout = new(type);
            NativeAllocation owner = new(layout, elementCount: 4);
            NativeAddress At(int index) => owner.AddressAt(layout.Extent * index, type);
            NativeMemory memory = new();
            NativeFuture future = new(Task.FromResult(value), logicalType: value.Type);
            memory.Write(At(0), new(future));
            At(0).Store(value);
            memory.Write(At(1), new(value));
            memory.Write(At(3), new(new NativeFuture(new TaskCompletionSource<NativeValue>().Task, logicalType: value.Type)));
            NativeStorage.ValidateRegion(At(0), layout, 3);
            NativeStorage.ValidateRegion(At(1), layout, 3);
            memory.CopyRegionTrusted(At(0), At(1), layout, 3);
            if (!ReferenceEquals(memory.Read(At(1)).Future, future) || At(1).IsInitialized)
                throw new InvalidOperationException($"{value.Kind}: region copy lost pending precedence after physical overwrite.");
            if (!memory.Read(At(2)).IsReady || memory.Read(At(2)).Value != value)
                throw new InvalidOperationException($"{value.Kind}: overlapping region copy lost a ready value.");
            if (memory.TryRead(At(3), out _))
                throw new InvalidOperationException($"{value.Kind}: uninitialized source did not erase the destination future.");
        }
        return values.Length * 2 * 3;
    }
}
