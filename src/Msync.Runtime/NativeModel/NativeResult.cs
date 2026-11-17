using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly partial struct NativeDatum
{
    private readonly NativeValue value;
    internal NativeDatum(NativeValue value) => (this.value, Future) = (value, null);
    internal NativeDatum(NativeFuture future) => (value, Future) = (default, future);
    internal NativeFuture? Future { get; }
    internal bool IsReady => Future is null;
    internal TypeRef? OptionalSourceType => Future is { } future ? future.OptionalSourceType : value.OptionalSourceType;
    internal TypeRef LogicalType => OptionalSourceType ?? throw new InvalidOperationException("A machine scalar has no source-language type.");
    internal NativeType PhysicalType => Future?.PhysicalType ?? value.PhysicalType;
    internal NativeValue Value => IsReady ? value : throw new InvalidOperationException("The operand is not ready.");
    internal ValueTask<NativeValue> ResolveAsync(CancellationToken token) => Future is { } future
        ? future.CompleteAsync(token) : new(value);

    internal NativeDatum Map(Func<NativeValue, NativeValue> transform, SourceSpan? source = null,
        TypeRef? resultType = null, NativeType? physicalType = null, bool preserveIdentity = false)
    {
        if (IsReady) return new(transform(value));
        NativeFuture dependency = Future!;
        NativeType resultPhysical = physicalType ?? (resultType is null ? dependency.PhysicalType : NativeType.FromTypeRef(resultType));
        return new(new NativeFuture(ApplyAsync(dependency, transform), [dependency], source: source,
            logicalType: resultPhysical.IsMachine ? null : resultType ?? dependency.OptionalSourceType,
            physicalType: resultPhysical,
            identity: preserveIdentity ? dependency.Identity : null));
    }

    private static async Task<NativeValue> ApplyAsync(NativeFuture dependency, Func<NativeValue, NativeValue> transform) =>
        transform(await dependency.CompleteAsync(CancellationToken.None).ConfigureAwait(false));
}

internal sealed class NativeFuture(Task<NativeValue> completion,
    IReadOnlyList<NativeFuture>? dependencies = null, bool transfer = false, SourceSpan? source = null,
    IReadOnlyList<NativeProcess>? processes = null, NativeFuture? parentAdmission = null,
    TypeRef? logicalType = null, NativeType? physicalType = null, NativeFuture? identity = null, NativeSubmission? submission = null, Task? resourceCompletion = null)
{
    private int failureCounted;
    private NativeWorkChildren? children;
    internal NativeWorkFrontier? Children => Volatile.Read(ref children)?.Snapshot();
    internal void AddChild(NativeFuture child)
    {
        if (LazyInitializer.EnsureInitialized(ref children, static () => new NativeWorkChildren()).Add(child))
            Submission?.RecordEscape(child.Source, child.Completion, process: false);
    }
    internal void AddChild(NativeProcess child)
    {
        if (LazyInitializer.EnsureInitialized(ref children, static () => new NativeWorkChildren()).Add(child))
            Submission?.RecordEscape(null, child.Completion, process: true);
    }
    internal Task<NativeValue> Completion { get; } = completion;
    internal TypeRef? OptionalSourceType { get; } = physicalType?.IsMachine == true ? null : logicalType ?? (transfer ? TypeRef.Bool : TypeRef.Any);
    internal TypeRef LogicalType => OptionalSourceType ?? throw new InvalidOperationException("A machine scalar future has no source-language type.");
    internal NativeType PhysicalType { get; } = physicalType ?? NativeType.FromTypeRef(logicalType ?? (transfer ? TypeRef.Bool : TypeRef.Any));
    internal NativeFuture Identity => identity ?? this;
    internal IReadOnlyList<NativeFuture> Dependencies { get; } = dependencies ?? [];
    internal bool IsTransfer { get; } = transfer;
    internal NativeSubmission? Submission { get; } = submission;
    internal SourceSpan? Source { get; } = source;
    internal IReadOnlyList<NativeProcess> Processes { get; } = processes ?? [];
    internal NativeFuture? ParentAdmission { get; } = parentAdmission;
    internal Task ResourceCompletion => resourceCompletion ?? Completion;
    internal bool HasProcessDependency => Submission?.Kind == NativeSubmissionKind.Process || Processes.Count != 0
        || Dependencies.Any(future => future.HasProcessDependency);
    internal bool DependsOnProcess(NativeProcess process) => Processes.Contains(process, ReferenceEqualityComparer.Instance)
        || Submission?.Kind == NativeSubmissionKind.Process && Completion.IsCompletedSuccessfully && ReferenceEquals(Completion.Result.AsProcess(), process)
        || Dependencies.Any(future => future.DependsOnProcess(process));
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

internal sealed class NativeMemory(RuntimeProfile.NativeStorageCounters? storageCounters = null)
{
    internal void RecordScalarWrite(NativeAddress address, NativeKind kind) => storageCounters?.Scalar(address.Type, kind);
    private ConditionalWeakTable<NativeAllocation, ConcurrentDictionary<NativeOffset, NativeFuture>>? pending;
    private long pendingRevision;
    private int activePending;
    internal long PendingRevision => Volatile.Read(ref pendingRevision);
    internal bool HasAnyPending
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref activePending) != 0;
    }
    internal bool IsPending(NativeAddress address) => TryPending(address, out _);

    internal void ResetFrame(NativeAllocation allocation)
    {
        if (Volatile.Read(ref allocation.PendingValues) != 0 && pending is { } table
            && table.TryGetValue(allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values)
            && table.Remove(allocation))
        {
            int removed = values.Count;
            if (removed > 0)
            {
                Interlocked.Add(ref activePending, -removed);
                Interlocked.Add(ref allocation.PendingValues, -removed);
            }
            Interlocked.Increment(ref pendingRevision);
        }
        allocation.Storage.ResetFrame();
    }

    internal NativeDatum Read(NativeAddress address)
    {
        return TryPending(address, out NativeFuture? future) ? ReadFuture(address, future!)
            : new(NativeStorage.LoadTrusted(address.Type, address));
    }

    internal NativeDatum ReadCompiled(NativeAddress address) => TryPending(address, out NativeFuture? future)
        ? ReadFuture(address, future!) : new(NativeStorage.LoadInitialized(address.Type, address));

    private NativeDatum ReadFuture(NativeAddress address, NativeFuture future)
    {
        if (!future.Completion.IsCompletedSuccessfully) return new(future);
        NativeDatum ready = new(future.Completion.Result);
        Write(address, ready);
        return ready;
    }

    internal bool TryRead(NativeAddress address, out NativeDatum value)
    {
        if (TryPending(address, out NativeFuture? future)) { value = new(future!); return true; }
        if (address.IsInitialized) { value = new(NativeStorage.LoadInitialized(address.Type, address)); return true; }
        value = default;
        return false;
    }

    internal void Write(NativeAddress address, NativeDatum value)
    {
        if (value.Future is { Completion.IsCompletedSuccessfully: true } completed)
            value = new(completed.Completion.Result);
        if (value.Future is { } future)
        {
            NativeStorage.ClearCompiled(address.Type, address);
            storageCounters?.Clear(address.Type);
            ConcurrentDictionary<NativeOffset, NativeFuture> values = Pending().GetValue(address.Allocation, static _ => new());
            // Increment before publishing so readers/removers never observe HasAnyPending == false for a live entry.
            Interlocked.Increment(ref address.Allocation.PendingValues);
            Interlocked.Increment(ref activePending);
            while (true)
            {
                if (values.TryAdd(address.Offset, future)) break;
                if (values.TryGetValue(address.Offset, out NativeFuture? existing) && values.TryUpdate(address.Offset, future, existing))
                {
                    Interlocked.Decrement(ref activePending);
                    Interlocked.Decrement(ref address.Allocation.PendingValues);
                    break;
                }
            }
            Interlocked.Increment(ref pendingRevision);
            return;
        }
        RemovePending(address);
        NativeStorage.StoreCompiled(address.Type, address, value.Value);
        storageCounters?.Write(address.Type);
    }

    internal void Clear(NativeAddress address)
    {
        RemovePending(address);
        NativeStorage.ClearCompiled(address.Type, address);
        storageCounters?.Clear(address.Type);
    }

    internal void WriteAddress(NativeAddress destination, NativeAddress value)
    {
        RemovePending(destination);
        NativeStorage.StoreAddressCompiled(destination, value);
        storageCounters?.Address(destination.Type);
    }

    private void CopyValue(NativeType type, NativeAddress source, NativeAddress destination)
    {
        NativeStorage.CopyCompiled(type, source, destination);
        storageCounters?.Copy(type);
    }

    private void CopyKindValue(NativeKind kind, NativeAddress source, NativeAddress destination)
    {
        NativeStorage.CopyKindCompiled(kind, source, destination);
        storageCounters?.CopyKind(source.Type, destination.Type, kind);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryCopyValue(NativeAddress source, NativeAddress destination)
    {
        if (source.Type.Equals(destination.Type))
        {
            RemovePending(destination);
            CopyValue(source.Type, source, destination);
            return true;
        }
        if (source.Type.IsMachine || destination.Type.IsMachine)
        {
            if (source.Type.Machine?.HasSameValueType(destination.Type.Machine) != true) return false;
            RemovePending(destination);
            CopyKindValue(source.Type.Kind, source, destination);
            return true;
        }
        NativeKind kind = source.Type.IsUnion ? source.Allocation.Storage.ReadKind(source.Offset) : source.Type.Kind;
        if (!destination.Type.Accepts(kind)) return false;
        RemovePending(destination);
        CopyKindValue(kind, source, destination);
        return true;
    }

    internal bool TryNarrowReady(NativeFrame frame, int sourceSlot, NativeAddress destination, NativeImage image, NativeType storage, int type, bool checkLayout = true)
    {
        NativeAddress source = frame.Location(sourceSlot);
        if (!frame.IsReady(sourceSlot, source)) return false;
        // Lowering assigns the result carrier; only the source alternative needs admission.
        NativeType sourceType = source.Type;
        if ((sourceType.IsMachine || storage.IsMachine) && !storage.Accepts(sourceType)) return false;
        NativeKind kind = sourceType.IsUnion ? source.Allocation.Storage.ReadKind(source.Offset) : sourceType.Kind;
        if (!storage.Accepts(kind)) return false;
        if (checkLayout && kind == NativeKind.Address
            && (!source.Allocation.Storage.TryReadAddressHeader(sourceType, source.Offset, out NativeAllocation allocation, out NativeOffset offset)
                || !image.AdmitsLayout(type, allocation, offset)))
            return false;
        RemovePending(destination);
        if (sourceType.Equals(destination.Type)) CopyValue(sourceType, source, destination);
        else CopyKindValue(kind, source, destination);
        return true;
    }

    internal bool TryConvertReady(NativeFrame frame, int sourceSlot, NativeAddress destination, NativeImage image, int targetType, bool checkLayout = true)
    {
        NativeAddress source = frame.Location(sourceSlot);
        if (!frame.IsReady(sourceSlot, source)) return false;
        NativeCodeType record = image.Program.Types[targetType];
        if (record.Element >= 0) return false;
        NativeType sourceType = source.Type;
        NativeKind kind = sourceType.IsUnion ? source.Allocation.Storage.ReadKind(source.Offset) : sourceType.Kind;
        if (kind == NativeKind.Range) return false;
        NativeType targetStorage = image.Type(targetType);
        if ((sourceType.IsMachine || targetStorage.IsMachine) && !targetStorage.Accepts(sourceType)) return false;
        if (!targetStorage.Accepts(kind)) return false;
        if (checkLayout && kind == NativeKind.Address
            && (!source.Allocation.Storage.TryReadAddressHeader(sourceType, source.Offset, out NativeAllocation allocation, out NativeOffset offset)
                || !image.AdmitsLayout(targetType, allocation, offset)))
            return false;
        RemovePending(destination);
        if (sourceType.Equals(destination.Type)) CopyValue(sourceType, source, destination);
        else CopyKindValue(kind, source, destination);
        return true;
    }

    internal void CopyCompiled(NativeAddress source, NativeAddress destination)
    {
        if (TryCopyPending(source, destination)) return;
        RemovePending(destination);
        // Initialization and kind admission are compiled facts; carrier adaptation is still needed.
        if (source.Type.Equals(destination.Type)) CopyValue(source.Type, source, destination);
        else
        {
            NativeKind kind = source.Type.IsUnion ? source.Allocation.Storage.ReadKind(source.Offset) : source.Type.Kind;
            CopyKindValue(kind, source, destination);
        }
    }

    internal void Copy(NativeAddress source, NativeAddress destination)
    {
        if (TryCopyPending(source, destination)) return;
        if (!source.IsInitialized || !TryCopyValue(source, destination))
            Write(destination, new(NativeStorage.LoadTrusted(source.Type, source)));
    }

    internal void CopyCompiled(NativeType type, NativeAddress source, NativeAddress destination)
    {
        if (TryCopyPending(source, destination)) return;
        RemovePending(destination);
        CopyValue(type, source, destination);
    }

    internal void Copy(NativeType type, NativeAddress source, NativeAddress destination)
    {
        if (TryCopyPending(source, destination)) return;
        if (!source.IsInitialized) { Write(destination, new(NativeStorage.LoadTrusted(source.Type, source))); return; }
        RemovePending(destination);
        CopyValue(type, source, destination);
    }

    private bool TryCopyPending(NativeAddress source, NativeAddress destination)
    {
        if (!TryPending(source, out NativeFuture? future)) return false;
        storageCounters?.PendingCopy(destination.Type);
        Write(destination, new(future!));
        return true;
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

    internal bool HasPending(NativeAllocation allocation) => Volatile.Read(ref allocation.PendingValues) != 0 && pending is { } table
        && table.TryGetValue(allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values) && !values.IsEmpty;

    internal bool TryPending(NativeAddress address, out NativeFuture? future)
    {
        if (Volatile.Read(ref address.Allocation.PendingValues) == 0)
        {
            future = null;
            return false;
        }
        if (pending is { } table && table.TryGetValue(address.Allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values)
            && values.TryGetValue(address.Offset, out future))
            return true;
        future = null;
        return false;
    }

    internal void RemovePending(NativeAddress address)
    {
        if (Volatile.Read(ref address.Allocation.PendingValues) != 0 && pending is { } table
            && table.TryGetValue(address.Allocation, out ConcurrentDictionary<NativeOffset, NativeFuture>? values)
            && values.TryRemove(address.Offset, out _))
        {
            Interlocked.Decrement(ref activePending);
            Interlocked.Decrement(ref address.Allocation.PendingValues);
        }
    }

    private ConditionalWeakTable<NativeAllocation, ConcurrentDictionary<NativeOffset, NativeFuture>> Pending()
    {
        if (Volatile.Read(ref pending) is { } existing) return existing;
        ConditionalWeakTable<NativeAllocation, ConcurrentDictionary<NativeOffset, NativeFuture>> created = new();
        return Interlocked.CompareExchange(ref pending, created, null) ?? created;
    }
}
