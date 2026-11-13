using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly struct NativeDatum
{
    private readonly NativeValue value;
    internal NativeDatum(NativeValue value) => (this.value, Future) = (value, null);
    internal NativeDatum(NativeFuture future) => (value, Future) = (default, future);
    internal NativeFuture? Future { get; }
    internal bool IsReady => Future is null;
    internal TypeRef LogicalType => Future?.LogicalType ?? value.Type;
    internal NativeType PhysicalType => Future?.PhysicalType ?? NativeType.Of(value.Kind);
    internal NativeValue Value => IsReady ? value : throw new InvalidOperationException("The operand is not ready.");
    internal ValueTask<NativeValue> ResolveAsync(CancellationToken token) => Future is { } future
        ? future.CompleteAsync(token) : new(value);

    internal NativeDatum Map(Func<NativeValue, NativeValue> transform, SourceSpan? source = null,
        TypeRef? resultType = null, NativeType? physicalType = null, bool preserveIdentity = false)
    {
        if (IsReady) return new(transform(value));
        NativeFuture dependency = Future!;
        return new(new NativeFuture(ApplyAsync(dependency, transform), [dependency], source: source,
            logicalType: resultType ?? dependency.LogicalType,
            physicalType: physicalType ?? (resultType is null ? dependency.PhysicalType : NativeType.FromTypeRef(resultType)),
            identity: preserveIdentity ? dependency.Identity : null));
    }

    private static async Task<NativeValue> ApplyAsync(NativeFuture dependency, Func<NativeValue, NativeValue> transform) =>
        transform(await dependency.CompleteAsync(CancellationToken.None).ConfigureAwait(false));
}

internal sealed class NativeFuture(Task<NativeValue> completion,
    IReadOnlyList<NativeFuture>? dependencies = null, bool transfer = false, SourceSpan? source = null,
    IReadOnlyList<NativeProcess>? processes = null, NativeFuture? parentAdmission = null,
    TypeRef? logicalType = null, NativeType? physicalType = null, NativeFuture? identity = null)
{
    private int failureCounted;
    internal Task<NativeValue> Completion { get; } = completion;
    internal TypeRef LogicalType { get; } = logicalType ?? (transfer ? TypeRef.Bool : TypeRef.Any);
    internal NativeType PhysicalType { get; } = physicalType ?? NativeType.FromTypeRef(logicalType ?? (transfer ? TypeRef.Bool : TypeRef.Any));
    internal NativeFuture Identity => identity ?? this;
    internal IReadOnlyList<NativeFuture> Dependencies { get; } = dependencies ?? [];
    internal bool IsTransfer { get; } = transfer;
    internal SourceSpan? Source { get; } = source;
    internal IReadOnlyList<NativeProcess> Processes { get; } = processes ?? [];
    internal NativeFuture? ParentAdmission { get; } = parentAdmission;
    internal void CountFailure(Action failedTransfer)
    {
        if (IsTransfer && Completion.IsCompletedSuccessfully
            && Completion.Result.Kind == NativeKind.Bool && !Completion.Result.AsBool()
            && Interlocked.CompareExchange(ref failureCounted, 1, 0) == 0) failedTransfer();
    }

    internal async ValueTask<NativeValue> CompleteAsync(CancellationToken token)
    {
        try { return await Completion.WaitAsync(token).ConfigureAwait(false); }
        catch (MsyncException error) when (error.Span is null && Source is not null)
        { throw new MsyncException(error.Message, Source, error); }
    }
}

internal sealed class NativeMemory
{
    private ConditionalWeakTable<NativeAllocation, ConcurrentDictionary<NativeOffset, NativeFuture>>? pending;
    private long pendingRevision;
    internal long PendingRevision => Volatile.Read(ref pendingRevision);
    internal bool IsPending(NativeAddress address) => TryPending(address, out _);

    internal NativeDatum Read(NativeAddress address) => TryPending(address, out NativeFuture? future)
        ? new(future!) : new(NativeStorage.LoadTrusted(address.Type, address));

    internal bool TryRead(NativeAddress address, out NativeDatum value)
    {
        if (TryPending(address, out NativeFuture? future)) { value = new(future!); return true; }
        if (address.IsInitialized) { value = new(NativeStorage.LoadTrusted(address.Type, address)); return true; }
        value = default;
        return false;
    }

    internal void Write(NativeAddress address, NativeDatum value)
    {
        if (value.Future is { } future)
        {
            NativeStorage.ClearTrusted(address.Type, address);
            Pending().GetValue(address.Allocation, static _ => new())[address.Offset] = future;
            Interlocked.Increment(ref pendingRevision);
            return;
        }
        RemovePending(address);
        NativeStorage.StoreTrusted(address.Type, address, value.Value);
    }

    internal void Clear(NativeAddress address)
    {
        RemovePending(address);
        NativeStorage.ClearTrusted(address.Type, address);
    }

    internal void WriteAddress(NativeAddress destination, NativeAddress value)
    {
        RemovePending(destination);
        NativeStorage.StoreAddress(destination, value);
    }

    internal bool TryCopyReady(NativeAddress source, NativeAddress destination)
    {
        if (!source.IsInitialized || IsPending(source) || !source.Type.Equals(destination.Type)) return false;
        RemovePending(destination);
        NativeStorage.CopyTrusted(source.Type, source, destination);
        return true;
    }

    internal void Copy(NativeAddress source, NativeAddress destination)
    {
        if (!TryCopyReady(source, destination)) Write(destination, Read(source));
    }

    internal void Copy(NativeType type, NativeAddress source, NativeAddress destination)
    {
        if (!source.IsInitialized || IsPending(source)) { Write(destination, Read(source)); return; }
        RemovePending(destination);
        NativeStorage.CopyTrusted(type, source, destination);
    }

    internal void CopyRegionTrusted(NativeAddress source, NativeAddress destination, NativeLayout layout, int count)
    {
        if (!HasPending(source.Allocation) && !HasPending(destination.Allocation))
        { NativeStorage.CopyRegionTrusted(source, destination, layout, count); return; }
        NativeFuture?[] snapshot = new NativeFuture?[checked(layout.Count * count)];
        int slot = 0;
        for (int element = 0; element < count; element++)
            foreach (NativeFieldLayout field in layout.Fields)
                TryPending(source.OffsetBy(layout.Extent * element + field.Offset, field.Type), out snapshot[slot++]);
        NativeStorage.CopyRegionTrusted(source, destination, layout, count);
        slot = 0;
        for (int element = 0; element < count; element++)
            foreach (NativeFieldLayout field in layout.Fields)
            {
                NativeAddress address = destination.OffsetBy(layout.Extent * element + field.Offset, field.Type);
                RemovePending(address);
                if (snapshot[slot++] is { } future) Write(address, new(future));
            }
    }

    internal void ClearRegionTrusted(NativeAddress address, NativeLayout layout, int count)
    {
        for (int element = 0; element < count; element++)
            foreach (NativeFieldLayout field in layout.Fields)
                Clear(address.OffsetBy(layout.Extent * element + field.Offset, field.Type));
    }

    internal bool HasPending(NativeAllocation allocation) => pending is { } table
        && table.TryGetValue(allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values) && !values.IsEmpty;

    internal bool TryPending(NativeAddress address, out NativeFuture? future)
    {
        future = null;
        return pending is { } table && table.TryGetValue(address.Allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values)
            && values.TryGetValue(address.Offset, out future);
    }

    internal void RemovePending(NativeAddress address)
    {
        if (pending is { } table && table.TryGetValue(address.Allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values))
            values.TryRemove(address.Offset, out _);
    }

    private ConditionalWeakTable<NativeAllocation, ConcurrentDictionary<NativeOffset, NativeFuture>> Pending()
    {
        if (Volatile.Read(ref pending) is { } existing) return existing;
        ConditionalWeakTable<NativeAllocation, ConcurrentDictionary<NativeOffset, NativeFuture>> created = new();
        return Interlocked.CompareExchange(ref pending, created, null) ?? created;
    }
}
