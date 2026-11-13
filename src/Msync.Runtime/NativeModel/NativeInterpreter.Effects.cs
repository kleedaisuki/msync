using System.Collections.Concurrent;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private readonly ResourceIndex resources = new();
    private readonly object resourceGate = new();
    private readonly ConcurrentDictionary<NativeFuture, byte> transfers = new(ReferenceEqualityComparer.Instance);
    private readonly AsyncLocal<NativeFuture?> admittedTransfer = new();
    private Task ordering = Task.CompletedTask;

    private NativeDatum ScheduleTransfer(NativeDatum source, NativeDatum destination, SourceSpan? span,
        NativeWorkScope owner, CancellationToken token, NativePlannedTransfer? plan = null,
        IReadOnlyList<Task>? predecessors = null, TypeKind sourceKind = TypeKind.Any)
    {
        bool cohort = source.IsReady ? source.Value.Kind == NativeKind.Range : sourceKind == TypeKind.Range;
        bool scalar = source.IsReady || sourceKind != TypeKind.Never && (sourceKind & TypeKind.Range) == 0;
        using SchedulingMeasurement measurement = MeasureScheduling("scheduler.admission", cohort);
        RuntimeTimelineScope? admission = measurement.Span;
        admission?.SetSource(span);
        admission?.SetAttribute("transfer.kind", cohort ? "cohort" : scalar ? "scalar" : "deferred");
        admission?.SetAttribute("transfer.source_kind", source.IsReady ? source.Value.Kind.ToString() : sourceKind.ToString());
        admission?.SetAttribute("transfer.destination", destination.IsReady ? destination.Value.ToString() : "deferred");
        if (source.IsReady && source.Value.Kind == NativeKind.Path)
            admission?.SetAttribute("transfer.source", source.Value.AsPath().ToString());
        try { return AdmitTransfer(source, destination, span, owner, token, plan, predecessors, sourceKind, admission); }
        catch (Exception error) { admission?.Fail(error); throw; }
    }

    private NativeDatum AdmitTransfer(NativeDatum source, NativeDatum destination, SourceSpan? span,
        NativeWorkScope owner, CancellationToken token, NativePlannedTransfer? plan,
        IReadOnlyList<Task>? predecessors, TypeKind sourceKind, RuntimeTimelineScope? admission)
    {
        NativeRuntimeContext origin = context.CaptureDirectory();
        NativeReachable roots = new(memory, GetRangeRoots, image);
        roots.Add(source);
        TaskCompletionSource<NativeValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ResourceIndex.Registration> registrations = [];
        HashSet<Task> dependencies;
        lock (resourceGate)
        {
            IReadOnlyList<ResourceKey> reads = TransferReads(source, origin, sourceKind);
            ResourceKey write = destination.IsReady && destination.Value.Kind is NativeKind.Path or NativeKind.String
                ? Key(origin.AsPath(destination.Value), origin) : new("*", "");
            dependencies = resources.Dependencies(reads, [write]);
            ExcludeAdmissions(dependencies);
            if (!ordering.IsCompletedSuccessfully) dependencies.Add(ordering);
            if (predecessors is not null) dependencies.UnionWith(predecessors);
            registrations.Add(resources.Add(completion.Task, null, write));
            foreach (ResourceKey read in reads) registrations.Add(resources.AddRead(completion.Task, read));
            RecordAdmission(plan, dependencies.Count);
            if (admission is not null)
            {
                admission.SetMetric("resources.reads", reads.Count);
                admission.SetMetric("resources.writes", 1);
                admission.SetMetric("dependencies.matched", dependencies.Count);
                for (int index = 0; index < reads.Count; index++) RecordSchedulingResource(admission, "read", index, reads[index]);
                RecordSchedulingResource(admission, "write", 0, write, true);
                context.Timeline!.Register(completion.Task, admission);
            }
        }
        List<NativeFuture> inputs = [];
        if (source.Future is { } sourceFuture) inputs.Add(sourceFuture);
        if (destination.Future is { } destinationFuture) inputs.Add(destinationFuture);
        NativeFuture future = new(completion.Task, inputs, transfer: true, source: span,
            processes: roots.Processes.ToArray(), parentAdmission: admittedTransfer.Value);
        owner.Add(future);
        transfers.TryAdd(future, 0);
        _ = CompleteTransferAsync(future, source, destination, origin, completion, dependencies, registrations, plan, token);
        return new(future);
    }

    private async Task CompleteTransferAsync(NativeFuture future, NativeDatum source, NativeDatum destination,
        NativeRuntimeContext origin, TaskCompletionSource<NativeValue> completion, HashSet<Task> dependencies,
        IReadOnlyList<ResourceIndex.Registration> registrations, NativePlannedTransfer? plan, CancellationToken token)
    {
        NativeFuture? previous = admittedTransfer.Value;
        admittedTransfer.Value = future;
        bool entered = false;
        try
        {
            if (dependencies.Count != 0) await AwaitSchedulingAsync(Task.WhenAll(dependencies), token, "transfer.resources", "relevant", dependencies);
            NativeValue value = await source.ResolveAsync(token);
            NativePathValue target = origin.AsPath(await destination.ResolveAsync(token));
            if (previous is null)
            {
                await AwaitSchedulingAsync(transferSlots.WaitAsync(token), CancellationToken.None, "transfer.capacity", "capacity");
                entered = true;
            }
            bool success = await ExecuteScheduledTransferAsync(value, target, origin, plan, token);
            completion.TrySetResult(NativeValue.FromBool(success));
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException or InvalidOperationException)
        {
            MsyncException diagnostic = error is MsyncException { Span: not null } selected ? selected
                : new(error.Message, future.Source, error);
            if (origin.ReportError is { } report) report(diagnostic);
            else await origin.Error.WriteLineAsync($"msync: {diagnostic.Message}");
            completion.TrySetResult(NativeValue.False);
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally
        {
            if (entered) transferSlots.Release();
            lock (resourceGate)
                foreach (ResourceIndex.Registration registration in registrations) resources.Remove(completion.Task, registration);
            transfers.TryRemove(future, out _);
            admittedTransfer.Value = previous;
        }
    }

    private static IReadOnlyList<ResourceKey> TransferReads(NativeDatum source, NativeRuntimeContext origin, TypeKind sourceKind)
    {
        if (!source.IsReady) return sourceKind != TypeKind.Never && (sourceKind & (TypeKind.Path | TypeKind.Range)) == 0
            ? [] : [new("*", "")];
        NativeValue value = source.Value;
        if (value.Kind == NativeKind.Path) return [Key(value.AsPath(), origin)];
        if (value.Kind != NativeKind.Range) return [];
        NativeRange range = value.AsRange();
        if (range.Selection is { } selection) return [Key(selection.Directory, origin)];
        if (range.SnapshotValues is not { } snapshot) return [new("*", "")];
        return snapshot.Where(static item => item.Kind == NativeKind.Path).Select(item => Key(item.AsPath(), origin)).ToArray();
    }

    private static ResourceKey Key(NativePathValue path, NativeRuntimeContext origin) =>
        path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote
            ? new(remote.ToString(), "")
            : new("local", ResourcePath.Resolve(path.LocalPath(origin.WorkingDirectory, origin.HomeDirectory)));

    private async ValueTask<NativeValue> InvokeNativeAsync(string name, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext origin, CancellationToken token)
    {
        (currentWork.Value ?? executionWork).Counters.Native(name);
        NativeBuiltinOperation operation = NativeBuiltins.Resolve(name);
        Msync.Core.NativeSignature signature = operation.Signature;
        bool deferredRead = DefersPathRead(signature);
        if (!deferredRead) await AwaitNativeDependenciesAsync(signature, arguments, origin, token);
        NativeValue result = await NativeBuiltins.InvokeAsync(operation, arguments, origin, InvokeAsync, token);
        if (!deferredRead || result.Kind != NativeKind.Range) return result;
        NativeRange range = result.AsRange();
        NativeRange wrapped = new(OrderedNativeItems(signature, arguments, origin, range), range.ItemType,
            [result, .. arguments], range.Selection, range.Resolve, physicalItemType: range.PhysicalItemType);
        return NativeValue.FromRange(wrapped);
    }

    private static bool DefersPathRead(Msync.Core.NativeSignature signature)
    {
        if (signature.Result.WithoutNull.Kind != TypeKind.Range
            || (signature.Behavior.GetValueOrDefault() & NativeBehavior.Deferred) == 0) return false;
        for (int index = 0; index < signature.Parameters.Count; index++)
            if ((signature.Parameters[index].Kind & TypeKind.Path) != 0
                && signature.AccessOfPathArgument(index) == NativePathAccess.Read) return true;
        return false;
    }

    private async IAsyncEnumerable<NativeValue> OrderedNativeItems(Msync.Core.NativeSignature signature, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext origin, NativeRange range,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        await AwaitNativeDependenciesAsync(signature, arguments, origin, token);
        await foreach (NativeValue value in range.Items.WithCancellation(token)) yield return value;
    }

    private async ValueTask AwaitNativeDependenciesAsync(Msync.Core.NativeSignature signature, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext origin, CancellationToken token)
    {
        if (signature.Effect == NativeEffect.Pure && signature.PathAccess is null) return;
        using RuntimeTimelineScope? observation = context.Timeline?.Begin("scheduler.native_dependencies");
        observation?.SetAttribute("native.name", signature.Name);
        observation?.SetMetric("pending.candidates", transfers.Count);
        HashSet<Task> dependencies = [];
        lock (resourceGate)
        {
            int resource = 0;
            for (int index = 0; index < arguments.Count && index < signature.Parameters.Count; index++)
            {
                NativeValue value = arguments[index];
                if ((signature.Parameters[index].Kind & TypeKind.Path) == 0 || value.Kind is not (NativeKind.Path or NativeKind.String)) continue;
                NativePathAccess access = signature.AccessOfPathArgument(index);
                if (access == NativePathAccess.None || access == NativePathAccess.ReadIfPath && value.Kind != NativeKind.Path) continue;
                ResourceKey key = Key(origin.AsPath(value), origin);
                resources.Collect(key, access == NativePathAccess.Write, dependencies);
                if (observation is not null) RecordSchedulingResource(observation, "observation", resource++, key, access == NativePathAccess.Write);
            }
            ExcludeAdmissions(dependencies);
            if (!ordering.IsCompletedSuccessfully) dependencies.Add(ordering);
        }
        observation?.SetMetric("dependencies.matched", dependencies.Count);
        if (dependencies.Count != 0)
        {
            Interlocked.Add(ref dependencyEdges, dependencies.Count);
            Task ready = Task.WhenAll(dependencies);
            if (!ready.IsCompleted) Interlocked.Increment(ref nativeHazardWaits);
            await AwaitSchedulingAsync(ready, token, "native.resources", "relevant", dependencies);
        }
    }

    private async ValueTask<NativeStep> ProcessCallAsync(NativeFrame frame, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeRuntimeContext origin = context.CaptureDirectory();
        NativeValue executable = await frame.GetDatum(instruction.A).ResolveAsync(token);
        NativeValue[] arguments = await ResolveArguments((instruction.Inputs ?? []).Select(frame.GetDatum).ToArray(), token);
        await AwaitSchedulingAsync(ordering, token, "process.ordering", "ordering");
        frame.Set(instruction.Result, await origin.ProcessCallAsync!(origin.AsPath(executable), arguments, origin, token));
        return new(pc + 1);
    }

    private async ValueTask<NativeStep> SelectPathAsync(NativeFrame frame, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeValue directory = await frame.GetDatum(instruction.A).ResolveAsync(token);
        NativeRange range = NativeHostIO.SelectPath(context.AsPath(directory), instruction.Symbol ?? "", context.CaptureDirectory());
        frame.Set(instruction.Result, NativeValue.FromRange(range));
        return new(pc + 1);
    }

    private async ValueTask<NativeStep> BarrierAsync(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        if (instruction.B != 0)
        {
            (activation.Regions ??= [])[instruction.Symbol ?? ""] = new(activation.Work.Pending, ReferenceEqualityComparer.Instance);
            return new(pc + 1);
        }
        if (instruction.Symbol is null && instruction.A == (int)BarrierDirection.Release) return new(pc + 1);
        NativeFuture[] finite = transfers.Keys.Where(static future => future.Processes.Count == 0).ToArray();
        if (instruction.A == (int)BarrierDirection.Acquire)
        {
            Task[] dependencies = finite.Select(static future => (Task)future.Completion).Prepend(ordering).ToArray();
            await AwaitSchedulingAsync(Task.WhenAll(dependencies), token, "fence.transfers", "global", dependencies);
            return new(pc + 1);
        }
        HashSet<NativeFuture>? before = null;
        activation.Regions?.Remove(instruction.Symbol ?? "", out before);
        IEnumerable<NativeFuture> associated = activation.Work.Pending.Where(future => before is not null && !before.Contains(future));
        ordering = Task.WhenAll(finite.Concat(associated).Select(static future => (Task)future.Completion).Prepend(ordering));
        return new(pc + 1);
    }

    private async ValueTask<NativeStep> AcquireAsync(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        if (instruction.A < 0)
        {
            for (NativeWorkScope? owner = activation.Work; owner is not null; owner = owner.Parent)
                await AwaitSchedulingAsync(owner.JoinAsync(token), token, "acquire.owner", "owner");
            return new(pc + 1);
        }
        NativeValue value = await activation.Frame.GetDatum(instruction.A).ResolveAsync(token);
        NativeProcess process = value.AsProcess();
        NativeProcess completed = await process.WaitAsync(token);
        NativeFuture[] flows = transfers.Keys.Where(future => future.Processes.Contains(process, ReferenceEqualityComparer.Instance)).ToArray();
        if (flows.Length != 0)
        {
            Task[] dependencies = flows.Select(static future => (Task)future.Completion).ToArray();
            await AwaitSchedulingAsync(Task.WhenAll(dependencies), token, "acquire.process_transfers", "relevant", dependencies);
        }
        if (instruction.Result >= 0) activation.Frame.Set(instruction.Result, NativeValue.FromProcess(completed));
        return new(pc + 1);
    }

    private void ExcludeAdmissions(HashSet<Task> dependencies)
    {
        for (NativeFuture? admission = admittedTransfer.Value; admission is not null; admission = admission.ParentAdmission)
            dependencies.Remove(admission.Completion);
    }
}
