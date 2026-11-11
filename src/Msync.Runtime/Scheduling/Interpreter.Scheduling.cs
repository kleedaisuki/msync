using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private readonly ResourceIndex resources;
    private long admittedTransfers;
    private long dependencyEdges;
    private long nativeHazardWaits;
    private long submittedPlans;
    private long submittedBatches;
    private long admittedOperations;
    private long eliminatedTransfers;
    public SchedulingStatistics SchedulingStatistics => new(admittedTransfers, resources.NodesVisited, dependencyEdges, nativeHazardWaits)
    {
        SubmittedPlans = submittedPlans,
        SubmittedBatches = submittedBatches,
        AdmittedOperations = admittedOperations,
        EliminatedTransfers = eliminatedTransfers,
        AdmissionNanoseconds = Interlocked.Read(ref admissionNanoseconds),
        CohortAdmissionNanoseconds = Interlocked.Read(ref cohortAdmissionNanoseconds),
        WaitNanoseconds = Interlocked.Read(ref waitNanoseconds)
    };
    private readonly List<PendingWork> pending;
    private readonly HashSet<ProcessValue> sessionProcesses;
    private readonly Dictionary<Task, IReadOnlyList<ProcessValue>> processDependencies;
    private readonly NamespaceProducerIndex namespaceProducers;
    private WorkOwner currentOwner = new();
    private Task standaloneOrdering = Task.CompletedTask;
    private Task ordering
    {
        get => sessionImage?.Ordering ?? standaloneOrdering;
        set { if (sessionImage is { } image) image.Ordering = value; else standaloneOrdering = value; }
    }

    private static Value ReadValue(Value value) => value is CellValue cell ? cell.Data : value;
    private static IReadOnlyList<Value> ValueArguments(IReadOnlyList<Value> arguments)
    {
        Value[]? values = null;
        for (var i = 0; i < arguments.Count; ++i)
        {
            if (arguments[i] is not CellValue cell) continue;
            values ??= arguments.ToArray();
            values[i] = cell.Data;
        }
        return values ?? arguments;
    }
    private static async ValueTask<Value> ResolveAsync(Value value)
        => value is DeferredValue deferred ? await deferred.Completion : value;
    private static async ValueTask<Value> ResolveResultAsync(Value value)
    {
        value = await ResolveAsync(value);
        return value is ProcessValue { Completion: not null } process ? await process.Completion : value;
    }

    private static string? PathEndpoint(Value value) => value switch
    {
        DeferredValue deferred => deferred.PathEndpoint,
        PathValue { Connection: { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection } => connection.ToString(),
        PathValue or StringValue => "local",
        _ => null
    };
    private static IReadOnlyList<ProcessValue> Processes(Value value) => value is DeferredValue deferred ? deferred.Processes : [];
    private static Value MapValue(Value value, TypeRef type, Func<Value, Value> apply, string? pathEndpoint = null)
    {
        if (value is not DeferredValue deferred) return apply(value);
        async Task<Value> Complete() => apply(await deferred.Completion);
        return new DeferredValue(Complete(), type, deferred.Processes, pathEndpoint);
    }
    private static Value CombineValues(Value left, Value right, TypeRef type, Func<Value, Value, Value> apply, string? pathEndpoint = null)
    {
        if (left is not DeferredValue && right is not DeferredValue) return apply(left, right);
        async Task<Value> Complete() => apply(await ResolveAsync(left), await ResolveAsync(right));
        return new DeferredValue(Complete(), type, Processes(left).Concat(Processes(right)).Distinct().ToArray(), pathEndpoint);
    }
    private static int FirstDeferredArgument(IReadOnlyList<Value> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
            if (arguments[index] is DeferredValue) return index;
        return -1;
    }

    private static ValueTask<IReadOnlyList<Value>> ResolveArgumentsAsync(IReadOnlyList<Value> arguments)
    {
        arguments = ValueArguments(arguments);
        int first = FirstDeferredArgument(arguments);
        return first < 0 ? ValueTask.FromResult(arguments) : ResolveDeferredArgumentsAsync(arguments, first);
    }

    private static async ValueTask<IReadOnlyList<Value>> ResolveDeferredArgumentsAsync(IReadOnlyList<Value> arguments, int first)
    {
        var resolved = arguments.ToArray();
        for (int index = first; index < resolved.Length; index++)
            if (resolved[index] is DeferredValue deferred) resolved[index] = await deferred.Completion;
        return resolved;
    }
    private ValueTask<Task<bool>> ScheduleTransferAsync(Value source, Value destination, SourceSpan? span, bool local,
        CancellationToken cancellationToken, bool discarded = false, TransferFootprint? footprint = null) =>
        ObserveScheduling ? ScheduleObservedTransferAsync(source, destination, span, local, cancellationToken, discarded, footprint)
            : ScheduleTransferCoreAsync(source, destination, span, local, cancellationToken, discarded, footprint);

    private async ValueTask<Task<bool>> ScheduleObservedTransferAsync(Value source, Value destination, SourceSpan? span, bool local,
        CancellationToken cancellationToken, bool discarded, TransferFootprint? footprint)
    {
        using var measurement = MeasureAdmission("scheduler.admission");
        var admission = measurement.Span;
        if (admission is not null)
        {
            admission.SetSource(span);
            admission.SetAttribute("transfer.kind", source is RangeValue ? "cohort" : "scalar");
            admission.SetAttribute("transfer.destination", destination is PathValue path ? path.ToString()
                : destination is StringValue text ? text.Data : "deferred");
            if (source is PathValue sourcePath) admission.SetAttribute("transfer.source", sourcePath.ToString());
            admission.SetMetric("pending.candidates", pending.Count);
            admission.SetMetric("transfer.local", local ? 1 : 0);
            admission.SetMetric("transfer.discarded", discarded ? 1 : 0);
        }
        try
        {
            return await ScheduleTransferCoreAsync(source, destination, span, local, cancellationToken, discarded, footprint, admission);
        }
        catch (Exception error) { admission?.Fail(error); throw; }
    }

    private async ValueTask<Task<bool>> ScheduleTransferCoreAsync(Value source, Value destination, SourceSpan? span, bool local,
        CancellationToken cancellationToken, bool discarded, TransferFootprint? footprint, RuntimeTimelineScope? admission = null)
    {
        // Resolve dependencies before starting I/O; the VM never runs mutable scalar bytecode in workers.
        var target = destination is DeferredValue ? null : context.AsPath(destination);
        RuntimeContext origin = context.CaptureDirectory();
        var reads = footprint?.Reads ?? (source is PathValue path ? new[] { Key(path) }
            : source is DeferredValue && (source.Type.Kind & TypeKind.Path) != 0 ? [new ResourceKey(PathEndpoint(source) ?? "*", "")] : []);
        var writes = footprint?.Writes ?? [target is null ? new ResourceKey((destination.Type.Kind & TypeKind.Path) != 0
            ? PathEndpoint(destination) ?? "*" : "local", "") : Key(target)];
        var dependencies = resources.Dependencies(reads, writes);
        List<Task>? namespaceDependencies = null;
        if (footprint is null)
        foreach (var dependency in dependencies)
        {
            if (!namespaceProducers.TryGetRoots(dependency, out var produced)) continue;
            if (produced.Any(root => root.Endpoint == "local"
                && (reads.Length != 0 && DescendsFrom(reads[0], root) || DescendsFrom(writes[0], root))))
                (namespaceDependencies ??= []).Add(dependency);
        }
        if (namespaceDependencies is not null)
        {
            // A prior directory/link publication can change physical aliases beneath its destination.
            await AwaitScheduled("transfer.namespace_publication", "relevant", namespaceDependencies);
            if (source is PathValue resolvedSource) reads = [Key(resolvedSource)];
            if (target is not null) writes = [Key(target)];
            dependencies = resources.Dependencies(reads, writes);
        }
        admittedTransfers++;
        dependencyEdges += dependencies.Count;
        if (admission is not null)
        {
            admission.SetMetric("resources.reads", reads.Length);
            admission.SetMetric("resources.writes", writes.Length);
            admission.SetMetric("dependencies.matched", dependencies.Count);
            for (int index = 0; index < reads.Length; index++) RecordResource(admission, "read", index, reads[index], false);
            for (int index = 0; index < writes.Length; index++) RecordResource(admission, "write", index, writes[index], true);
        }
        var predecessor = ordering;
        async Task<bool> Run()
        {
            await AwaitScheduled("transfer.ordering", "ordering", predecessor);
            if (dependencies.Count != 0) await AwaitScheduled("transfer.resources", "relevant", dependencies);
            var value = await ResolveAsync(source);
            var actualTarget = target ?? origin.AsPath(await ResolveAsync(destination));
            await AwaitScheduled("transfer.capacity", "capacity", slots.WaitAsync(cancellationToken));
            try { return await TransferAsync(value, actualTarget, span, local, origin, cancellationToken); }
            finally { slots.Release(); }
        }
        var completion = Run();
        if (admission is not null)
        {
            bool ready = completion.IsCompleted;
            admission.SetMetric("completion.ready_at_admission", ready ? 1 : 0);
            if (!ready)
            {
                // A synchronous async-method result may reuse a runtime-cached Task.
                admission.SetMetric("task.id", completion.Id);
                context.Timeline!.Register(completion, admission);
            }
        }
        IReadOnlyList<ProcessValue> processes = source is DeferredValue && destination is DeferredValue
            ? Processes(source).Concat(Processes(destination)).Distinct().ToArray()
            : source is DeferredValue ? Processes(source) : Processes(destination);
        foreach (var dependency in dependencies)
            if (processDependencies.TryGetValue(dependency, out var related)) processes = processes.Concat(related).Distinct().ToArray();
        var work = new PendingWork(completion, processes, discarded);
        if (processes.Count != 0) processDependencies.Add(completion, processes);
        if (footprint is null)
            work.Registration = resources.Add(completion, reads.Length == 0 ? null : reads[0], writes[0]);
        else
        {
            List<ResourceIndex.Registration> registrations = new(reads.Length + writes.Length);
            foreach (var read in reads) registrations.Add(resources.AddRead(completion, read));
            foreach (var write in writes) registrations.Add(resources.Add(completion, null, write));
            work.Registrations = registrations;
        }
        if (source is PathValue or RangeValue || source is DeferredValue && (source.Type.Kind & TypeKind.Path) != 0)
            namespaceProducers.Add(completion, writes);
        pending.Add(work);
        (currentOwner.Tasks ??= []).Add(work);
        return completion;
    }
    private static bool DescendsFrom(ResourceKey child, ResourceKey parent)
    {
        if (child.Endpoint != parent.Endpoint || child.Path.Length == 0) return false;
        bool windows = parent.Endpoint == "local" && OperatingSystem.IsWindows();
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = windows ? parent.Path.AsSpan().TrimEnd("/\\".AsSpan()) : parent.Path.AsSpan().TrimEnd('/');
        return child.Path.Length > prefix.Length && child.Path.AsSpan().StartsWith(prefix, comparison)
            && (child.Path[prefix.Length] == '/' || windows && child.Path[prefix.Length] == '\\');
    }
    private ValueTask AwaitNativeDependenciesAsync(NativeSignature signature, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken) =>
        context.Timeline is null ? AwaitNativeDependenciesCoreAsync(signature, arguments, cancellationToken)
            : ObserveNativeDependenciesAsync(signature, arguments, cancellationToken);

    private async ValueTask ObserveNativeDependenciesAsync(NativeSignature signature, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken)
    {
        using var observation = context.Timeline!.Begin("scheduler.native_dependencies");
        observation.SetAttribute("native.name", signature.Name);
        observation.SetMetric("pending.candidates", pending.Count);
        try { await AwaitNativeDependenciesCoreAsync(signature, arguments, cancellationToken, observation); }
        catch (Exception error) { observation.Fail(error); throw; }
    }

    private async ValueTask AwaitNativeDependenciesCoreAsync(NativeSignature signature, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken, RuntimeTimelineScope? observation = null)
    {
        await AwaitScheduled("native.ordering", "ordering", ordering);
        if (pending.Count == 0 || !resources.HasRegistrations) return;
        List<(PathValue Path, bool Writes)>? operands = null;
        for (int index = 0; index < Math.Min(arguments.Count, signature.Parameters.Count); index++)
        {
            if ((signature.Parameters[index].Kind & TypeKind.Path) == 0
                || arguments[index] is not (PathValue or StringValue)) continue;
            NativePathAccess access = signature.AccessOfPathArgument(index);
            if (access == NativePathAccess.None || access == NativePathAccess.ReadIfPath && arguments[index] is not PathValue) continue;
            (operands ??= []).Add((context.AsPath(arguments[index]), access == NativePathAccess.Write));
        }
        if (operands is null) return;
        observation?.SetMetric("resources.operands", operands.Count);
        await AwaitNamespacePublicationsAsync(operands.Select(operand => operand.Path));
        var filesystem = new PlanFileSystem(context, planResources, cancellationToken);
        HashSet<Task> publications = [];
        int resourceIndex = 0;
        foreach (var operand in operands)
        {
            ResourceKey key = Key(filesystem.Resource(operand.Path, true));
            if (observation is not null) RecordResource(observation, "publication", resourceIndex++, key, operand.Writes);
            resources.Collect(key, operand.Writes, publications);
        }
        observation?.SetMetric("publications.matched", publications.Count);
        if (publications.Count != 0)
        {
            nativeHazardWaits++;
            await AwaitScheduled("native.resource_publication", "relevant", publications);
        }
        bool follow = signature.Name is not ("fs.symlink_status" or "fs.is_symlink" or "fs.read_symlink"
            or "fs.remove" or "fs.remove_all" or "fs.rename");
        HashSet<Task> dependencies = [];
        resourceIndex = 0;
        foreach (var operand in operands)
            foreach (var resource in filesystem.ObservationResources(operand.Path, follow))
            {
                ResourceKey key = Key(resource);
                if (observation is not null) RecordResource(observation, "observation", resourceIndex++, key, operand.Writes);
                resources.Collect(key, operand.Writes, dependencies);
            }
        observation?.SetMetric("dependencies.matched", dependencies.Count);
        if (dependencies.Count != 0)
        {
            nativeHazardWaits++;
            await AwaitScheduled("native.resources", "relevant", dependencies);
        }
    }
    private async ValueTask FenceAsync(int direction, int beginRegion, string? region)
    {
        if (beginRegion != 0)
        {
            (currentOwner.AfterRegions ??= new())[region ?? ""] = currentOwner.Tasks?.Count ?? 0;
            return;
        }
        if (region is null && direction == (int)BarrierDirection.Release) return;
        // A fence orders finite work, not unrelated live program lifetime.
        if (direction == (int)BarrierDirection.Acquire)
        {
            await AwaitScheduled("fence.ordering", "ordering", ordering);
            await JoinTransfersAsync(includeProcessFlows: false);
            return;
        }
        var first = currentOwner.AfterRegions is { } regions && regions.Remove(region ?? "", out var boundary)
            ? boundary : currentOwner.Tasks?.Count ?? 0;
        var associated = currentOwner.Tasks?.Skip(first).Select(work => (Task)work.Completion) ?? [];
        ordering = Task.WhenAll(pending.Where(work => work.Processes.Count == 0)
            .Select(work => (Task)work.Completion).Concat(associated).Prepend(ordering));
    }
    private async ValueTask<Value> AcquireAsync(Value value)
    {
        if (value is not ProcessValue process) throw new MsyncException($"acquire requires Process, received {value.Type}");
        var completed = process.Completion is null ? process : await AwaitScheduledResultAsync("acquire.process", "relevant", process.Completion);
        var flows = pending.Where(work => work.Processes.Any(related => ReferenceEquals(related, process))).ToArray();
        if (flows.Length != 0)
        {
            await AwaitScheduled("acquire.process_transfers", "relevant", flows.Select(work => work.Completion));
            foreach (var work in flows) { Retire(work); pending.Remove(work); }
        }
        sessionProcesses.Remove(process);
        if (flows.Any(work => work.RequireSuccess && !work.Completion.Result))
            throw new MsyncException("One or more file transfers failed; completed files remain in place and the operation can be retried");
        return completed;
    }
    private async ValueTask JoinOwnerAsync(WorkOwner owner)
    {
        if (owner.Processes is { Count: > 0 })
            await AwaitScheduled("owner.processes", "owner",
                owner.Processes.Where(process => process.Completion is not null).Select(process => process.Completion!));
        if (owner.Tasks is { Count: > 0 })
        {
            await AwaitScheduled("owner.transfers", "owner", owner.Tasks.Select(work => work.Completion));
            foreach (var work in owner.Tasks) Retire(work);
            if (owner.Tasks.Any(work => work.RequireSuccess && !work.Completion.Result))
                throw new MsyncException("One or more file transfers failed; completed files remain in place and the operation can be retried");
        }
    }
    private async ValueTask JoinTransfersAsync(bool includeProcessFlows = true, string? fallback = null)
    {
        var work = pending.Where(item => includeProcessFlows || item.Processes.Count == 0).ToArray();
        if (work.Length == 0) return;
        await AwaitScheduled(fallback ?? (includeProcessFlows ? "join.transfers" : "fence.transfers"),
            fallback is not null ? "fallback" : includeProcessFlows ? "global" : "ordering",
            work.Select(item => item.Completion));
        foreach (var item in work) Retire(item);
        if (work.Any(item => item.RequireSuccess && !item.Completion.Result))
            throw new MsyncException("One or more file transfers failed; completed files remain in place and the operation can be retried");
        pending.RemoveAll(work => work.Completion.IsCompletedSuccessfully);
    }
    private void Retire(PendingWork work)
    {
        if (!work.Completion.IsCompletedSuccessfully) return;
        if (work.Registration is { } registration) resources.Remove(work.Completion, registration);
        if (work.Registrations is { } registrations)
            foreach (var item in registrations) resources.Remove(work.Completion, item);
        work.Registration = null;
        work.Registrations = null;
        processDependencies.Remove(work.Completion);
        namespaceProducers.Remove(work.Completion);
    }
    private async ValueTask JoinAllAsync(string? fallback = null)
    {
        if (sessionProcesses.Count != 0)
            await AwaitScheduled(fallback ?? "join.processes", fallback is null ? "global" : "fallback",
                sessionProcesses.Where(process => process.Completion is not null).Select(process => process.Completion!));
        await JoinTransfersAsync(fallback: fallback);
        sessionProcesses.RemoveWhere(process => process.Completion?.IsCompletedSuccessfully ?? true);
    }
    private ValueTask DrainAsync() => JoinAllAsync();
    private ResourceKey Key(PathValue path)
    {
        if (path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
            // Relative, home and symlink projections are unknown without endpoint observation.
            // Do not authenticate merely to prove two future remote operations independent.
            return new(remote.ToString(), "");
        return new("local", ResourcePath.Resolve(path.LocalPath(context.WorkingDirectory, context.HomeDirectory)));
    }
}
