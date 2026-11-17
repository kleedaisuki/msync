using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

internal readonly unsafe struct NativeRootBank
{
    private readonly NativeStorageSlice<NativeRoot> managed;
    private readonly nint* edges;
    private readonly NativeGuestObjectTable? objects;
    internal int Length => managed.Length;
    internal NativeRoot[] Buffer => managed.Buffer;
    internal NativeGuestObjectTable? ReferenceTable => objects;
    internal nint GuestAddress => (nint)edges;
    internal ReadOnlySpan<NativeRoot> HostRoots => managed.Slice(0, Length);
    internal ReadOnlySpan<nint> GuestEdges => edges is null ? [] : new(edges, Length);
    internal nint ReadGuestEdge(int index) => edges is null ? 0 : edges[index];

    internal NativeRootBank(NativeRoot[] roots) : this(roots, 0, roots.Length) { }

    internal NativeRootBank(NativeRoot[] roots, int offset, int length,
        NativeGuestObjectTable? objects = null, nint edges = 0)
    {
        managed = new(roots, offset, length);
        this.edges = (nint*)edges;
        this.objects = objects;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAllocation? ReadAllocation(int index)
    {
        nint edge = edges is null ? 0 : edges[index];
        return edge == 0 ? managed[index].Allocation : objects!.Resolve(edge);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeEnvironment? ReadEnvironment(int index)
    {
        nint edge = edges is null ? 0 : edges[index];
        return edge == 0 ? managed[index].Environment : objects!.Resolve(edge).GuestEnvironment;
    }

    internal NativeRoot ReadHost(int index) => managed[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteAllocation(int index, NativeAllocation value)
    {
        nint edge = edges is not null && ReferenceEquals(value.GuestReferences, objects)
            ? value.Storage.RawAddress : 0;
        WriteEdge(index, edge, new() { Allocation = value });
    }

    internal void WriteEnvironment(int index, NativeEnvironment? value) =>
        WriteEdge(index, edges is null ? 0 : EnvironmentEdge(value), new() { Environment = value });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteHost(int index, NativeRoot value) => WriteEdge(index, 0, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteEdge(int index, nint edge, NativeRoot host)
    {
        if (edges is not null) edges[index] = edge;
        managed[index] = edge == 0 ? host : default;
    }

    internal nint CallableCell(int index)
    {
        nint edge = edges is null ? 0 : edges[index];
        return edge != 0
            ? (edge & NativeGuestObjectTable.EnvironmentTag) != 0 ? objects!.Resolve(edge).MachineCaptures : 0
            : managed[index].Value is NativeEnvironment environment ? environment.MachineCaptures : 0;
    }

    private NativeRoot Materialize(int index)
    {
        if (edges is not null)
        {
            nint edge = edges[index];
            if (edge != 0)
            {
                NativeAllocation allocation = objects!.Resolve(edge);
                return (edge & NativeGuestObjectTable.EnvironmentTag) == 0 ? new() { Allocation = allocation }
                    : new() { Environment = allocation.GuestEnvironment };
            }
        }
        return managed[index];
    }

    private nint Edge(object? target)
    {
        if (target is NativeAllocation allocation && ReferenceEquals(allocation.GuestReferences, objects))
            return allocation.Storage.RawAddress;
        return EnvironmentEdge(target as NativeEnvironment);
    }

    private nint EnvironmentEdge(NativeEnvironment? environment)
    {
        if (environment is null || environment.Address.Offset != default) return 0;
        NativeAllocation owner = environment.Address.Allocation;
        if (!ReferenceEquals(owner.GuestReferences, objects)) return 0;
        // Keep exact closure identity. Additional views of the same environment
        // cannot use this slot unless they are that very instance.
        NativeEnvironment? existing = Volatile.Read(ref owner.GuestEnvironment)
            ?? Interlocked.CompareExchange(ref owner.GuestEnvironment, environment, null);
        return existing is null || ReferenceEquals(existing, environment)
            ? owner.Storage.RawAddress | NativeGuestObjectTable.EnvironmentTag : 0;
    }

    internal void Clear(int start, int count)
    {
        managed.Slice(start, count).Clear();
        if (edges is not null) new Span<nint>(edges + start, count).Clear();
    }

    internal void ClearHost() => managed.Clear();
    internal void Clear() => Clear(0, Length);

    internal void CopyTo(int start, NativeRootBank destination, int offset, int count)
    {
        if (count == 0) return;
        if ((edges is null && destination.edges is null)
            || (edges is not null && destination.edges is not null && ReferenceEquals(objects, destination.objects)))
        {
            if (count < 3)
            {
                NativeRoot first = managed[start], second = count == 2 ? managed[start + 1] : default;
                destination.managed[offset] = first;
                if (count == 2) destination.managed[offset + 1] = second;
                if (edges is not null)
                {
                    nint firstEdge = edges[start], secondEdge = count == 2 ? edges[start + 1] : 0;
                    destination.edges[offset] = firstEdge;
                    if (count == 2) destination.edges[offset + 1] = secondEdge;
                }
            }
            else
            {
                managed.Slice(start, count).CopyTo(destination.managed.Slice(offset, count));
                if (edges is not null) new ReadOnlySpan<nint>(edges + start, count).CopyTo(new(destination.edges + offset, count));
            }
            return;
        }
        for (int index = 0; index < count; index++)
        {
            NativeRoot host = Materialize(start + index);
            destination.WriteEdge(offset + index, destination.edges is null ? 0 : destination.Edge(host.Value), host);
        }
    }
}
