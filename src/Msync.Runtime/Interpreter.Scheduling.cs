using Msync.Core;

namespace Msync.Runtime;

public readonly record struct SchedulingStatistics(long AdmittedTransfers, long ResourceNodesVisited, long DependencyEdges, long NativeHazardWaits);

public sealed partial class Interpreter
{
    private sealed class WorkOwner
    {
        public List<PendingWork>? Tasks;
        public List<ProcessValue>? Processes;
        public Dictionary<string, int>? AfterRegions;
    }
    private sealed record DeferredValue(Task<Value> Completion, TypeRef ResultType, IReadOnlyList<ProcessValue> Processes, string? PathEndpoint = null) : Value
    {
        public override TypeRef Type => ResultType;
    }
    private sealed record PendingWork(Task<bool> Completion, IReadOnlyList<ProcessValue> Processes, bool RequireSuccess)
    {
        public ResourceIndex.Registration? Registration;
    }
    private sealed record ResourceKey(string Endpoint, string Path);
    private readonly ResourceIndex resources = new();
    private long admittedTransfers;
    private long dependencyEdges;
    private long nativeHazardWaits;
    public SchedulingStatistics SchedulingStatistics => new(admittedTransfers, resources.NodesVisited, dependencyEdges, nativeHazardWaits);
    private readonly List<PendingWork> pending = [];
    private readonly HashSet<ProcessValue> sessionProcesses = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Task, IReadOnlyList<ProcessValue>> processDependencies = [];
    private readonly Dictionary<Task, ResourceKey> namespaceProducers = [];
    private WorkOwner currentOwner = new();
    private Task ordering = Task.CompletedTask;

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
        var reads = source is PathValue path ? new[] { Key(path) }
            : source is DeferredValue && (source.Type.Kind & TypeKind.Path) != 0 ? [new ResourceKey(PathEndpoint(source) ?? "*", "")] : [];
        var writes = new[] { target is null ? new ResourceKey((destination.Type.Kind & TypeKind.Path) != 0 ? PathEndpoint(destination) ?? "*" : "local", "") : Key(target) };
        var dependencies = resources.Dependencies(reads, writes);
        List<Task>? namespaceDependencies = null;
        foreach (var dependency in dependencies)
        {
            if (!namespaceProducers.TryGetValue(dependency, out var produced) || produced.Endpoint != "local") continue;
            if (reads.Length != 0 && DescendsFrom(reads[0], produced) || DescendsFrom(writes[0], produced))
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
            var actualTarget = target ?? context.AsPath(await ResolveAsync(destination));
            await slots.WaitAsync(cancellationToken);
            try { return await TransferAsync(value, actualTarget, span, local, cancellationToken); }
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
            namespaceProducers.Add(completion, writes[0]);
        pending.Add(work);
        (currentOwner.Tasks ??= []).Add(work);
        return completion;
    }
    private static bool DescendsFrom(ResourceKey child, ResourceKey parent)
    {
        if (child.Endpoint != parent.Endpoint || child.Path.Length == 0) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = parent.Path.AsSpan().TrimEnd(Path.DirectorySeparatorChar);
        return child.Path.Length > prefix.Length && child.Path.AsSpan().StartsWith(prefix, comparison)
            && child.Path[prefix.Length] == Path.DirectorySeparatorChar;
    }
    private async ValueTask AwaitNativeDependenciesAsync(string name, IReadOnlyList<Value> arguments)
    {
        if (!NativeSignatures.TryGet(name, out var signature)) { await JoinTransfersAsync(); return; }
        var effects = EffectAnalysis.NativeEffects(name);
        if ((effects & (InstructionEffect.Read | InstructionEffect.Write)) == 0) return;
        await ordering;
        var paths = arguments.Where((value, index) => index < signature.Parameters.Count
            && (signature.Parameters[index].Kind & TypeKind.Path) != 0 && value is PathValue or StringValue)
            .Select(value => Key(context.AsPath(value))).ToArray();
        var dependencies = signature.Effect == NativeEffect.Write ? resources.Dependencies([], paths) : resources.Dependencies(paths, []);
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
        // A fence orders finite work, not unrelated live program lifetime.
        if (direction == 1) { await ordering; await JoinTransfersAsync(includeProcessFlows: false); return; }
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
            foreach (var work in flows) Retire(work);
        }
        sessionProcesses.Remove(process);
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
        if (work.Registration is not { } registration || !work.Completion.IsCompletedSuccessfully) return;
        resources.Remove(work.Completion, registration);
        work.Registration = null;
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
    private sealed class ResourceIndex
    {
        public sealed class Node(StringComparer comparer, Node? parent = null, string? name = null)
        {
            public readonly Dictionary<string, Node> Children = new(comparer);
            public readonly HashSet<Task> Readers = [];
            public readonly HashSet<Task> Writers = [];
            public readonly Node? Parent = parent;
            public readonly string? Name = name;
        }
        public readonly record struct Registration(Node? Reader, Node Writer);
        private readonly Dictionary<string, Node> roots = new(StringComparer.Ordinal);
        private Node? unknown;
        public long NodesVisited { get; private set; }
        private static StringComparer Comparer(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private Node Root(ResourceKey key)
        {
            if (key.Endpoint == "*") return unknown ??= new(StringComparer.Ordinal);
            if (!roots.TryGetValue(key.Endpoint, out var root)) roots.Add(key.Endpoint, root = new(Comparer(key.Endpoint)));
            return root;
        }
        private static IEnumerable<string> Parts(ResourceKey key) => key.Path.Split(key.Endpoint == "local" && OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
        public HashSet<Task> Dependencies(IReadOnlyList<ResourceKey> reads, IReadOnlyList<ResourceKey> writes)
        {
            HashSet<Task> tasks = [];
            foreach (var key in reads) Collect(key, false, tasks);
            foreach (var key in writes) Collect(key, true, tasks);
            return tasks;
        }
        private static void AddActive(HashSet<Task> source, HashSet<Task> target)
        {
            source.RemoveWhere(task => task.IsCompletedSuccessfully);
            target.UnionWith(source);
        }
        private void Collect(ResourceKey key, bool writing, HashSet<Task> tasks)
        {
            if (unknown is not null)
            {
                AddActive(unknown.Writers, tasks);
                if (writing) AddActive(unknown.Readers, tasks);
            }
            if (key.Endpoint == "*")
            {
                foreach (var root in roots.Values)
                {
                    NodesVisited++;
                    AddActive(root.Writers, tasks);
                    if (writing) AddActive(root.Readers, tasks);
                    CollectDescendants(root, writing, tasks);
                }
                return;
            }
            var node = Root(key);
            NodesVisited++;
            AddActive(node.Writers, tasks);
            if (writing) AddActive(node.Readers, tasks);
            foreach (var part in Parts(key))
            {
                if (!node.Children.TryGetValue(part, out node!)) return;
                NodesVisited++;
                AddActive(node.Writers, tasks);
                if (writing) AddActive(node.Readers, tasks);
            }
            CollectDescendants(node, writing, tasks);
        }
        private void CollectDescendants(Node node, bool writing, HashSet<Task> tasks)
        {
            foreach (var child in node.Children.Values)
            {
                NodesVisited++;
                AddActive(child.Writers, tasks);
                if (writing) AddActive(child.Readers, tasks);
                CollectDescendants(child, writing, tasks);
            }
        }
        public Registration Add(Task completion, ResourceKey? read, ResourceKey write)
        {
            var reader = read is null ? null : Leaf(read);
            var writer = Leaf(write);
            reader?.Readers.Add(completion);
            writer.Writers.Add(completion);
            return new(reader, writer);
        }
        public void Remove(Task completion, Registration registration)
        {
            registration.Reader?.Readers.Remove(completion);
            registration.Writer.Writers.Remove(completion);
            if (registration.Reader is { } reader) Prune(reader);
            Prune(registration.Writer);
        }
        private static void Prune(Node node)
        {
            while (node.Parent is { } parent && node.Children.Count == 0 && node.Readers.Count == 0 && node.Writers.Count == 0)
            {
                // A query can clear completed tasks before their owner's lease is retired.
                if (!parent.Children.TryGetValue(node.Name!, out var child) || !ReferenceEquals(child, node)) return;
                parent.Children.Remove(node.Name!);
                node = parent;
            }
        }
        private Node Leaf(ResourceKey key)
        {
            var node = Root(key);
            NodesVisited++;
            foreach (var part in Parts(key))
            {
                if (!node.Children.TryGetValue(part, out var child)) node.Children.Add(part, child = new(Comparer(key.Endpoint), node, part));
                node = child;
                NodesVisited++;
            }
            return node;
        }
    }
}
