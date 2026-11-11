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
        EliminatedTransfers = eliminatedTransfers
    };
    private readonly List<PendingWork> pending;
    private readonly HashSet<ProcessValue> sessionProcesses;
    private readonly Dictionary<Task, IReadOnlyList<ProcessValue>> processDependencies;
    private readonly Dictionary<Task, IReadOnlyList<ResourceKey>> namespaceProducers;
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
    private static async ValueTask<IReadOnlyList<Value>> ResolveArgumentsAsync(IReadOnlyList<Value> arguments)
    {
        arguments = ValueArguments(arguments);
        Value[]? resolved = null;
        for (var i = 0; i < arguments.Count; ++i)
        {
            if (arguments[i] is not DeferredValue) continue;
            resolved ??= arguments.ToArray();
            resolved[i] = await ResolveAsync(arguments[i]);
        }
        return resolved ?? arguments;
    }
    private async ValueTask<Task<bool>> ScheduleTransferAsync(Value source, Value destination, SourceSpan? span, bool local, CancellationToken cancellationToken, bool discarded = false)
    {
        // Resolve dependencies before starting I/O; the VM never runs mutable scalar bytecode in workers.
        var target = destination is DeferredValue ? null : context.AsPath(destination);
        RuntimeContext origin = context.CaptureDirectory();
        var reads = source is PathValue path ? new[] { Key(path) }
            : source is DeferredValue && (source.Type.Kind & TypeKind.Path) != 0 ? [new ResourceKey(PathEndpoint(source) ?? "*", "")] : [];
        var writes = new[] { target is null ? new ResourceKey((destination.Type.Kind & TypeKind.Path) != 0 ? PathEndpoint(destination) ?? "*" : "local", "") : Key(target) };
        var dependencies = resources.Dependencies(reads, writes);
        List<Task>? namespaceDependencies = null;
        foreach (var dependency in dependencies)
        {
            if (!namespaceProducers.TryGetValue(dependency, out var produced)) continue;
            if (produced.Any(root => root.Endpoint == "local"
                && (reads.Length != 0 && DescendsFrom(reads[0], root) || DescendsFrom(writes[0], root))))
                (namespaceDependencies ??= []).Add(dependency);
        }
        if (namespaceDependencies is not null)
        {
            // A prior directory/link publication can change physical aliases beneath its destination.
            await Task.WhenAll(namespaceDependencies);
            if (source is PathValue resolvedSource) reads = [Key(resolvedSource)];
            if (target is not null) writes = [Key(target)];
            dependencies = resources.Dependencies(reads, writes);
        }
        admittedTransfers++;
        dependencyEdges += dependencies.Count;
        var predecessor = ordering;
        async Task<bool> Run()
        {
            await predecessor;
            if (dependencies.Count != 0) await Task.WhenAll(dependencies);
            var value = await ResolveAsync(source);
            var actualTarget = target ?? origin.AsPath(await ResolveAsync(destination));
            await slots.WaitAsync(cancellationToken);
            try { return await TransferAsync(value, actualTarget, span, local, origin, cancellationToken); }
            finally { slots.Release(); }
        }
        var completion = Run();
        IReadOnlyList<ProcessValue> processes = source is DeferredValue && destination is DeferredValue
            ? Processes(source).Concat(Processes(destination)).Distinct().ToArray()
            : source is DeferredValue ? Processes(source) : Processes(destination);
        foreach (var dependency in dependencies)
            if (processDependencies.TryGetValue(dependency, out var related)) processes = processes.Concat(related).Distinct().ToArray();
        var work = new PendingWork(completion, processes, discarded);
        if (processes.Count != 0) processDependencies.Add(completion, processes);
        work.Registration = resources.Add(completion, reads.Length == 0 ? null : reads[0], writes[0]);
        if (source is PathValue || source is DeferredValue && (source.Type.Kind & TypeKind.Path) != 0)
            namespaceProducers.Add(completion, [writes[0]]);
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
    private async ValueTask AwaitNativeDependenciesAsync(string name, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken = default)
    {
        if (NativeEvaluation.DefersFileRead(name)) return;
        if (!NativeSignatures.TryGet(name, out var signature)) { await JoinTransfersAsync(); return; }
        if ((signature.Behavior.GetValueOrDefault() & NativeBehavior.LocalMemory) != 0) return;
        var effects = InstructionEffects.NativeEffects(name);
        if ((effects & (InstructionEffect.Read | InstructionEffect.Write)) == 0) return;
        await ordering;
        List<PathValue>? operands = null;
        for (int index = 0; index < Math.Min(arguments.Count, signature.Parameters.Count); index++)
            if ((signature.Parameters[index].Kind & TypeKind.Path) != 0
                && arguments[index] is PathValue or StringValue)
                (operands ??= []).Add(context.AsPath(arguments[index]));
        if (operands is null) return;
        await AwaitNamespacePublicationsAsync(operands);
        var filesystem = new PlanFileSystem(context, planResources, cancellationToken);
        var entries = operands.Select(path => Key(filesystem.Resource(path, true))).ToArray();
        var publications = signature.Effect == NativeEffect.Write
            ? resources.Dependencies([], entries) : resources.Dependencies(entries, []);
        if (publications.Count != 0)
        {
            nativeHazardWaits++;
            await Task.WhenAll(publications);
        }
        bool follow = name is not ("fs.symlink_status" or "fs.is_symlink" or "fs.read_symlink"
            or "fs.remove" or "fs.remove_all" or "fs.rename");
        var paths = operands.SelectMany(path => filesystem.ObservationResources(path, follow)).Select(Key).ToArray();
        var dependencies = signature.Effect == NativeEffect.Write
            ? resources.Dependencies([], paths) : resources.Dependencies(paths, []);
        if (dependencies.Count != 0)
        {
            nativeHazardWaits++;
            await Task.WhenAll(dependencies);
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
        if (direction == (int)BarrierDirection.Acquire) { await ordering; await JoinTransfersAsync(includeProcessFlows: false); return; }
        var first = currentOwner.AfterRegions is { } regions && regions.Remove(region ?? "", out var boundary)
            ? boundary : currentOwner.Tasks?.Count ?? 0;
        var associated = currentOwner.Tasks?.Skip(first).Select(work => (Task)work.Completion) ?? [];
        ordering = Task.WhenAll(pending.Where(work => work.Processes.Count == 0)
            .Select(work => (Task)work.Completion).Concat(associated).Prepend(ordering));
    }
    private async ValueTask<Value> AcquireAsync(Value value)
    {
        if (value is not ProcessValue process) throw new MsyncException($"acquire requires Process, received {value.Type}");
        var completed = process.Completion is null ? process : await process.Completion;
        var flows = pending.Where(work => work.Processes.Any(related => ReferenceEquals(related, process))).ToArray();
        if (flows.Length != 0)
        {
            await Task.WhenAll(flows.Select(work => work.Completion));
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
            await Task.WhenAll(owner.Processes.Where(process => process.Completion is not null).Select(process => process.Completion!));
        if (owner.Tasks is { Count: > 0 })
        {
            await Task.WhenAll(owner.Tasks.Select(work => work.Completion));
            foreach (var work in owner.Tasks) Retire(work);
            if (owner.Tasks.Any(work => work.RequireSuccess && !work.Completion.Result))
                throw new MsyncException("One or more file transfers failed; completed files remain in place and the operation can be retried");
        }
    }
    private async ValueTask JoinTransfersAsync(bool includeProcessFlows = true)
    {
        var work = pending.Where(item => includeProcessFlows || item.Processes.Count == 0).ToArray();
        if (work.Length == 0) return;
        await Task.WhenAll(work.Select(item => item.Completion));
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
        resources.RetireTask(work.Completion);
        processDependencies.Remove(work.Completion);
        namespaceProducers.Remove(work.Completion);
    }
    private async ValueTask JoinAllAsync()
    {
        if (sessionProcesses.Count != 0)
            await Task.WhenAll(sessionProcesses.Where(process => process.Completion is not null).Select(process => process.Completion!));
        await JoinTransfersAsync();
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
