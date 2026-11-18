using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

internal readonly unsafe struct NativeRootBank
{
    // A hidden binding can name its frame without allocating a host entry.
    internal const nint SelfBinding = NativeStorageTypeWord.Tag | NativeGuestObjectTable.EnvironmentTag;
    private readonly NativeStorageSlice<NativeRoot> managed;
    private readonly nint* edges;
    private readonly NativeGuestObjectTable? objects;

    internal int Length { get; }
    internal NativeGuestObjectTable? ReferenceTable => objects;
    internal nint GuestAddress => (nint)edges;
    internal ReadOnlySpan<NativeRoot> HostRoots => edges is null ? managed.Slice(0, Length) : [];
    internal ReadOnlySpan<nint> GuestEdges => edges is null ? [] : new(edges, Length);

    internal nint ReadGuestEdge(int index)
    {
        nint edge = edges is null ? 0 : edges[index];
        return NativeGuestObjectTable.IsObjectEdge(edge) ? edge : 0;
    }

    internal NativeRootBank(NativeRoot[] roots) : this(roots, 0, roots.Length) { }

    internal NativeRootBank(NativeRoot[] roots, int offset, int length)
    {
        managed = new(roots, offset, length);
        Length = length;
    }

    internal NativeRootBank(nint* edges, int length, NativeGuestObjectTable objects)
    {
        this.edges = edges;
        this.objects = objects;
        Length = length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAllocation? ReadAllocation(int index)
    {
        if (edges is null) return managed[index].Allocation;
        nint edge = edges[index];
        if (edge == 0) return null;
        if (NativeGuestObjectTable.IsStackEdge(edge)) return objects!.ResolveStack(edge);
        return NativeGuestObjectTable.IsObjectEdge(edge) ? objects!.Resolve(edge) : ReadNativeHost(index, edge).Allocation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAllocation? ReadBinding(int index, NativeAllocation frame)
    {
        if (edges is null) return managed[index].Allocation;
        nint edge = edges[index];
        if (edge == SelfBinding) return frame;
        if (edge == 0) return null;
        if (NativeGuestObjectTable.IsStackEdge(edge)) return objects!.ResolveStack(edge);
        return NativeGuestObjectTable.IsObjectEdge(edge) ? objects!.Resolve(edge) : ReadNativeHost(index, edge).Allocation;
    }

    internal void WriteBinding(int index, NativeAllocation frame, NativeAllocation owner)
    {
        if (edges is not null && ReferenceEquals(frame, owner)) Replace(index, SelfBinding);
        else WriteAllocation(index, owner);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeEnvironment? ReadEnvironment(int index)
    {
        if (edges is null) return managed[index].Environment;
        nint edge = edges[index];
        return NativeGuestObjectTable.IsObjectEdge(edge) ? objects!.Resolve(edge).GuestEnvironment : ReadNativeHost(index, edge).Environment;
    }

    internal NativeRoot ReadHost(int index) => edges is null ? managed[index] : ReadNativeHost(index, edges[index]);

    private NativeRoot ReadNativeHost(int index, nint edge) => NativeHostReference.IsHost(edge)
        ? objects!.HostReferences.Read((nint)(edges + index))
        : NativeGuestObjectTable.IsPlanEdge(edge) ? new() { Range = NativeRange.FromPlan(objects!, edge) }
        : NativeGuestObjectTable.IsTextEdge(edge) ? new() { Text = new string(NativeGuestObjectTable.TextSpan(edge)) }
        : NativeStorageTypeWord.IsType(edge) ? new() { StorageType = NativeStorageTypeWord.Decode(edge) } : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteAllocation(int index, NativeAllocation value)
    {
        if (edges is not null && ReferenceEquals(value.GuestReferences, objects))
            Replace(index, value.Storage.RawAddress);
        else if (edges is not null && value.Storage.StackAddressable && ReferenceEquals(value.EdgeTable, objects))
            Replace(index, value.Storage.StackEdge());
        else WriteHost(index, new() { Allocation = value });
    }

    internal void WriteEnvironment(int index, NativeEnvironment? value)
    {
        nint edge = edges is null ? 0 : EnvironmentEdge(value);
        if (edge != 0) Replace(index, edge);
        else WriteHost(index, new() { Environment = value });
    }

    internal void WriteHost(int index, NativeRoot value)
    {
        if (edges is null) { managed[index] = value; return; }
        if (value.Value is string text && objects!.AllocateText is { } allocate)
        {
            Replace(index, allocate(text) | NativeGuestObjectTable.TextTag);
            return;
        }
        if (value.Value is null) { Replace(index, 0); return; }
        objects!.HostReferences.Set((nint)(edges + index), value);
        edges[index] = NativeHostReference.Marker;
    }

    internal nint PlanEdge(int index)
    {
        nint edge = edges is null ? 0 : edges[index];
        return NativeGuestObjectTable.IsPlanEdge(edge) ? edge : 0;
    }

    internal void WritePlan(int index, NativeGuestObjectTable arena, nint edge)
    {
        if (edges is not null && ReferenceEquals(arena, objects)) Replace(index, edge);
        else WriteHost(index, new() { Range = NativeRange.FromPlan(arena, edge) });
    }

    internal nint TextEdge(int index)
    {
        nint edge = edges is null ? 0 : edges[index];
        return NativeGuestObjectTable.IsTextEdge(edge) ? edge : 0;
    }

    internal void WriteTextEdge(int index, nint edge, object? owner)
    {
        if (edges is not null && ReferenceEquals(owner, objects))
            Replace(index, edge);
        else
            WriteHost(index, new() { Text = new string(NativeGuestObjectTable.TextSpan(edge)) });
    }

    internal void WriteStorageType(int index, NativeType? type)
    {
        if (edges is not null && type is { IsMachine: false })
            Replace(index, NativeStorageTypeWord.Encode(type));
        else WriteHost(index, new() { StorageType = type });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Replace(int index, nint edge)
    {
        nint old = edges[index];
        edges[index] = edge;
        if (NativeHostReference.IsHost(old)) objects!.HostReferences.Remove((nint)(edges + index));
    }

    internal nint CallableCell(int index)
    {
        if (edges is null) return managed[index].Value is NativeEnvironment host ? host.MachineCaptures : 0;
        nint edge = edges[index];
        if (NativeGuestObjectTable.IsObjectEdge(edge) && (edge & NativeGuestObjectTable.EnvironmentTag) != 0) return Volatile.Read(ref NativeGuestObjectTable.CaptureCell(edge & ~NativeGuestObjectTable.EnvironmentTag));
        return ReadNativeHost(index, edge).Value is NativeEnvironment environment ? environment.MachineCaptures : 0;
    }

    private NativeRoot Materialize(int index)
    {
        if (edges is null) return managed[index];
        nint edge = edges[index];
        if (NativeGuestObjectTable.IsStackEdge(edge)) return new() { Allocation = objects!.ResolveStack(edge) };
        if (!NativeGuestObjectTable.IsObjectEdge(edge)) return ReadNativeHost(index, edge);
        NativeAllocation allocation = objects!.Resolve(edge);
        return (edge & NativeGuestObjectTable.EnvironmentTag) == 0 ? new() { Allocation = allocation }
            : new() { Environment = allocation.GuestEnvironment };
    }

    private nint EnvironmentEdge(NativeEnvironment? environment)
    {
        if (environment is null || environment.Address.Offset != default) return 0;
        NativeAllocation owner = environment.Address.Allocation;
        if (!ReferenceEquals(owner.GuestReferences, objects)) return 0;
        // Keep additional views of the same storage distinguishable at the host boundary.
        NativeEnvironment? existing = Volatile.Read(ref owner.GuestEnvironment)
            ?? Interlocked.CompareExchange(ref owner.GuestEnvironment, environment, null);
        return existing is null || ReferenceEquals(existing, environment)
            ? owner.Storage.RawAddress | NativeGuestObjectTable.EnvironmentTag : 0;
    }

    internal void Clear(int start, int count)
    {
        if (count == 0) return;
        if (edges is null) { managed.Slice(start, count).Clear(); return; }
        for (int i = 0; i < count; i++) Replace(start + i, 0);
    }

    internal void Clear() => Clear(0, Length);

    internal void CopyTo(int start, NativeRootBank destination, int offset, int count)
    {
        if (count == 0) return;
        if (edges is null && destination.edges is null)
        {
            managed.Slice(start, count).CopyTo(destination.managed.Slice(offset, count));
            return;
        }
        if (edges is not null && destination.edges is not null && ReferenceEquals(objects, destination.objects))
        {
            CopyNative(start, destination, offset, count);
            return;
        }
        for (int index = 0; index < count; index++)
        {
            NativeRoot host = Materialize(start + index);
            if (destination.edges is not null && host.Value is NativeAllocation allocation)
                destination.WriteAllocation(offset + index, allocation);
            else if (destination.edges is not null && host.Value is NativeEnvironment environment)
                destination.WriteEnvironment(offset + index, environment);
            else if (destination.edges is not null && host.Value is NativeType storageType)
                destination.WriteStorageType(offset + index, storageType);
            else destination.WriteHost(offset + index, host);
        }
    }

    private void CopyNative(int start, NativeRootBank destination, int offset, int count)
    {
        nint* source = edges + start, target = destination.edges + offset;
        if (source == target) return;
        if (count <= 2)
        {
            nint a = source[0], b = count == 2 ? source[1] : 0;
            nint oldB = count == 2 ? target[1] : 0;
            if (!NativeHostReference.IsHost(a | b | target[0] | oldB))
            {
                target[0] = a;
                if (count == 2) target[1] = b;
                return;
            }
        }

        // Copy backward only when publishing an early destination could
        // replace an input slot that we have not visited yet.
        if (target > source && target < source + count)
            for (int i = count - 1; i >= 0; --i) CopySlot(start + i, destination, offset + i);
        else
            for (int i = 0; i < count; ++i) CopySlot(start + i, destination, offset + i);
    }

    private void CopySlot(int start, NativeRootBank destination, int offset)
    {
        nint edge = edges[start];
        if (NativeHostReference.IsHost(edge)) destination.WriteHost(offset, ReadNativeHost(start, edge));
        else destination.Replace(offset, edge);
    }
}
