using System.Diagnostics;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private readonly record struct PlanNodeResult(Task<IReadOnlyList<TransferBatchOutcome>> Completion, int Offset);
    private sealed record PlanSubmission(PlanNodeResult[] Results);

    private PlanSubmission SubmitPlan(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        var batches = ExecutionBatches(plan);
        submittedPlans++;
        submittedBatches += batches.Count;
        eliminatedTransfers += plan.EliminatedTransfers;
        context.Observe?.Invoke(new("execution-plan", "submitted",
            Detail: $"nodes={plan.Transfers.Count}; batches={batches.Count}; eliminated={plan.EliminatedTransfers}"));
        var outcomes = new TaskCompletionSource<IReadOnlyList<TransferBatchOutcome>>[batches.Count];
        var completed = new Task<bool>[batches.Count];
        var nodeBatches = new int[plan.Transfers.Count];
        var results = new PlanNodeResult[plan.Transfers.Count];
        bool[]? observedNodes = plan.ResultTransfers is null ? null : new bool[plan.Transfers.Count];
        if (observedNodes is not null)
            foreach (int node in plan.ResultTransfers!) observedNodes[node] = true;
        for (int batch = 0; batch < batches.Count; batch++)
        {
            outcomes[batch] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completed[batch] = SuccessAsync(outcomes[batch].Task, batches[batch], observedNodes);
            for (int offset = 0; offset < batches[batch].Count; offset++)
            {
                int node = batches[batch][offset];
                nodeBatches[node] = batch;
                results[node] = new(outcomes[batch].Task, offset);
            }
        }

        var inputs = new HashSet<Task>[batches.Count];
        bool hasPriorWork = pending.Count != 0 && resources.HasRegistrations;
        for (int batch = 0; batch < batches.Count; batch++)
        {
            var dependencies = inputs[batch] = [ordering];
            foreach (int node in batches[batch])
            {
                var transfer = plan.Transfers[node];
                if (hasPriorWork)
                {
                    foreach (var read in transfer.Reads) resources.Collect(Key(read), false, dependencies);
                    foreach (var write in transfer.Writes) resources.Collect(Key(write), true, dependencies);
                }
                foreach (int predecessor in transfer.Predecessors)
                    dependencies.Add(completed[nodeBatches[predecessor]]);
            }
            dependencyEdges += dependencies.Count - 1;
        }

        RuntimeContext origin = context.CaptureDirectory();
        for (int batch = 0; batch < batches.Count; batch++)
        {
            var ids = batches[batch];
            var completion = completed[batch];
            List<ResourceIndex.Registration> registrations = new(ids.Count);
            List<ResourceKey>? produced = null;
            foreach (int node in ids)
            {
                var transfer = plan.Transfers[node];
                for (int write = 0; write < transfer.Writes.Count; write++)
                    registrations.Add(resources.Add(completion,
                        write == 0 && transfer.Reads.Count != 0 ? Key(transfer.Reads[0]) : null,
                        Key(transfer.Writes[write])));
                for (int read = 1; read < transfer.Reads.Count; read++)
                    registrations.Add(resources.AddRead(completion, Key(transfer.Reads[read])));
                if (transfer.Kind is PlannedTransferKind.CopyDirectory or PlannedTransferKind.CopyLink
                    or PlannedTransferKind.GenericCopy or PlannedTransferKind.EnsureDirectory)
                    (produced ??= []).AddRange(transfer.Writes.Select(Key));
            }
            if (produced is not null) namespaceProducers.Add(completion, produced);
            bool requireSuccess = observedNodes is null || ids.Any(node => !observedNodes[node]);
            var work = new PendingWork(completion, [], requireSuccess) { Registrations = registrations };
            pending.Add(work);
            (currentOwner.Tasks ??= []).Add(work);
            admittedOperations += ids.Count;
            admittedTransfers += ids.Count(id => plan.Transfers[id].Kind is not
                (PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata));
            _ = RunPlanBatchAsync(plan, ids, inputs[batch], outcomes[batch], results, origin, cancellationToken);
        }
        return new(results);
    }

    private List<IReadOnlyList<int>> ExecutionBatches(ExecutionPlan plan)
    {
        List<IReadOnlyList<int>> output = [];
        foreach (var batch in plan.Batches)
        {
            if (batch.Endpoint != "local" || batch.Transfers.Count <= 1)
            {
                output.Add(batch.Transfers);
                continue;
            }
            int workers = Math.Min(transferConcurrency, batch.Transfers.Count);
            int size = (batch.Transfers.Count + workers - 1) / workers;
            for (int start = 0; start < batch.Transfers.Count; start += size)
                output.Add(batch.Transfers.Skip(start).Take(size).ToArray());
        }
        return output;
    }

    private async Task RunPlanBatchAsync(ExecutionPlan plan, IReadOnlyList<int> ids,
        HashSet<Task> dependencies, TaskCompletionSource<IReadOnlyList<TransferBatchOutcome>> completion,
        PlanNodeResult[] predecessors, RuntimeContext origin, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(dependencies);
            List<int>? ready = null;
            for (int offset = 0; offset < ids.Count; offset++)
            {
                var operation = plan.Transfers[ids[offset]];
                if (operation.Kind == PlannedTransferKind.ApplyDirectoryMetadata
                    && !await SuccessfulPredecessorsAsync(operation, predecessors))
                    ready ??= Enumerable.Range(0, offset).ToList();
                else ready?.Add(offset);
            }
            var active = ready is null ? ids : ready.Select(offset => ids[offset]).ToArray();
            IReadOnlyList<TransferBatchOutcome> values;
            long started = origin.Observe is null ? 0 : Stopwatch.GetTimestamp();
            IReadOnlyList<TransferBatchOutcome> executed = [];
            if (active.Count != 0)
            {
                await slots.WaitAsync(cancellationToken);
                List<IDisposable>? selected = null;
                try
                {
                    var items = active.Select(id => plan.Transfers[id])
                        .Select(transfer => new TransferBatchItem(transfer.Source, transfer.Destination, transfer.Kind, transfer.Mode)).ToArray();
                    selected = origin.DryRun ? null : EnterPlanSessions(items, cancellationToken);
                    executed = await TransferEngine.CopyBatchAsync(items, origin, cancellationToken);
                }
                finally { DisposeSessions(selected); slots.Release(); }
            }
            if (ready is null) values = executed;
            else
            {
                var combined = Enumerable.Repeat(new TransferBatchOutcome(false, Executed: false), ids.Count).ToArray();
                for (int offset = 0; offset < ready.Count; offset++) combined[ready[offset]] = executed[offset];
                values = combined;
            }
            origin.Observe?.Invoke(new("transfer-batch", values.All(value => value.Success) ? "success" : "failure",
                Detail: $"nodes={ids.Count}",
                DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
            for (int offset = 0; offset < ids.Count; offset++)
            {
                var transfer = plan.Transfers[ids[offset]];
                var outcome = values[offset];
                if (outcome.Error is { } error)
                    await ReportPlannedFailureAsync(transfer, error, origin);
                else origin.Observe?.Invoke(new(PlanPhase(transfer), outcome.Executed ? outcome.Success ? "success" : "failure" : "blocked",
                    SourceLabel(transfer.Source), transfer.Destination.ToString()));
            }
            completion.SetResult(values);
        }
        catch (OperationCanceledException error) { completion.SetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.SetException(error); }
    }

    private async ValueTask ReportPlannedFailureAsync(PlannedTransfer transfer, Exception error, RuntimeContext origin)
    {
        Interlocked.Increment(ref failedTransfers);
        string source = SourceLabel(transfer.Source);
        origin.Observe?.Invoke(new(PlanPhase(transfer), "failure", source, transfer.Destination.ToString(), error.ToString()));
        string action = transfer.Kind switch
        {
            PlannedTransferKind.EnsureDirectory => $"Cannot create directory '{transfer.Destination}'.",
            PlannedTransferKind.ApplyDirectoryMetadata => $"Cannot apply directory metadata from '{source}' to '{transfer.Destination}'.",
            _ => $"Cannot transfer {source} -> {transfer.Destination}."
        };
        var diagnostic = new MsyncException(action, transfer.Span, error);
        if (origin.ReportError is { } report) report(diagnostic);
        else await origin.Error.WriteLineAsync($"{diagnostic.Message} {error.Message}");
    }

    private static async Task<bool> SuccessAsync(Task<IReadOnlyList<TransferBatchOutcome>> completion,
        IReadOnlyList<int> nodes, bool[]? observed)
    {
        var values = await completion;
        for (int index = 0; index < nodes.Count; index++)
            if ((observed is null || !observed[nodes[index]]) && !values[index].Success) return false;
        return true;
    }

    private static async ValueTask<bool> ReadPlanResultAsync(ExecutionPlan plan, PlanSubmission submission)
    {
        bool result = true;
        foreach (int node in plan.ResultTransfers!)
        {
            var selected = submission.Results[node];
            result &= (await selected.Completion)[selected.Offset].Success;
        }
        return result;
    }

    private static ResourceKey Key(PlanResource resource) => new(resource.Endpoint, resource.Path);

    private static string PlanPhase(PlannedTransfer operation) => operation.Kind switch
    {
        PlannedTransferKind.EnsureDirectory => "directory-create",
        PlannedTransferKind.ApplyDirectoryMetadata => "directory-metadata",
        _ => "transfer"
    };

    private static async ValueTask<bool> SuccessfulPredecessorsAsync(PlannedTransfer operation, PlanNodeResult[] results)
    {
        foreach (int predecessor in operation.Predecessors)
        {
            var result = results[predecessor];
            if (!(await result.Completion)[result.Offset].Success) return false;
        }
        return true;
    }

    private List<IDisposable>? EnterPlanSessions(IReadOnlyList<TransferBatchItem> items, CancellationToken cancellationToken)
    {
        HashSet<ConnectionValue>? connections = null;
        List<IDisposable>? scopes = null;
        try
        {
            foreach (var item in items)
            {
                if (item.Source is PathValue { Connection.Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } source)
                    Enter(source.Connection!);
                if (item.Destination.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } target)
                    Enter(target);
            }
            return scopes;
        }
        catch { DisposeSessions(scopes); throw; }

        void Enter(ConnectionValue connection)
        {
            if (!(connections ??= []).Add(connection)) return;
            (scopes ??= []).Add(planResources.Enter(connection, cancellationToken));
        }
    }

    private static void DisposeSessions(List<IDisposable>? scopes)
    {
        for (int index = (scopes?.Count ?? 0) - 1; index >= 0; index--) scopes![index].Dispose();
    }

    private async ValueTask DisposePlanResourcesAsync()
    {
        await planResources.DisposeAsync();
    }

    private IDisposable? EnterPathSession(IReadOnlyList<Value> values, CancellationToken cancellationToken) =>
        values.OfType<PathValue>().FirstOrDefault(path => path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            is { Connection: { } connection } ? planResources.Enter(connection, cancellationToken) : null;

    private async ValueTask AwaitNamespacePublicationsAsync(IEnumerable<PathValue> paths)
    {
        if (NamespaceDependencies(paths) is { } dependencies)
            await Task.WhenAll(dependencies);
    }

    private List<Task>? NamespaceDependencies(IEnumerable<PathValue> paths)
    {
        if (namespaceProducers.Count == 0) return null;
        HashSet<string> entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl) continue;
            string? name = path.LocalPath(context.WorkingDirectory, context.HomeDirectory);
            while (name is not null)
            {
                string? parent = Path.GetDirectoryName(name);
                entries.Add(parent is null ? name : Path.Combine(ResourcePath.Resolve(parent), Path.GetFileName(name)));
                name = parent;
            }
        }
        var dependencies = namespaceProducers.Where(pair => !pair.Key.IsCompleted
            && pair.Value.Any(root => root.Endpoint == "local" && entries.Contains(root.Path)))
            .Select(pair => pair.Key).ToArray();
        return dependencies.Length == 0 ? null : dependencies.ToList();
    }

}
