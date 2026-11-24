using System.Diagnostics;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal void SubmitPreparedTransfer(int binding, IReadOnlyList<HpirPreparedTransfer> transfers,
        ExecutionOptions? options, BindingContext? context, HpirWorkSite site, string functionName, bool shared)
    {
        shared |= retainedFileResources?.HasRegistrations == true;
        FileTransfers owner = Files(options, shared ? context : null);
        Task?[] scheduled = owner.PreparedTasks ??= new Task?[transfers.Count];
        HpirPreparedTransfer transfer = transfers[binding];
        Task? ordering = Ordering;
        int predecessorCount = transfer.Predecessors.Count;
        int count = predecessorCount + (ordering is null ? 0 : 1);
        Task[] predecessors = count == 0 ? [] : new Task[count];
        for (int i = 0; i < predecessorCount; i++)
            predecessors[i] = scheduled[transfer.Predecessors[i]]
                ?? throw new MsyncException("A prepared transfer predecessor was not submitted.", site.Source);
        if (ordering is not null) predecessors[^1] = ordering;
        HpirHostResult<bool> job = shared ? SubmitSharedTransfer(binding, transfer, owner, predecessors, site, functionName)
            : Submit<bool>(HpirWorkKind.Transfer,
                token => ExecutePreparedTransfer(binding, transfer, owner, predecessors, site, functionName, token), site,
                retainResult: true);
        scheduled[binding] = jobs[job.JobId].Completion;
    }

    private HpirHostResult<bool> SubmitSharedTransfer(int binding, HpirPreparedTransfer transfer,
        FileTransfers owner, Task[] predecessors, HpirWorkSite site, string functionName)
    {
        FileResources frontier = owner.NeedResources();
        FileKeys? read = transfer.Kind == HpirTransferSource.Path ? FileKeysFor(transfer.Paths[0]) : null;
        FileKeys write = FileKeysFor(transfer.Destination);
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.admit");
        if (trace is not null)
        {
            trace.SetAttribute("transfer.origin", "prepared");
            trace.SetAttribute("transfer.kind", transfer.Kind.ToString());
            trace.SetAttribute("transfer.destination", transfer.Destination);
            trace.SetMetric("transfer.binding", binding);
        }
        return Submit<bool>(HpirWorkKind.Transfer, token =>
        {
            FileAdmission admission = frontier.Admit(read, write, sharedTransfer: true);
            ReportAdmission(site, trace, admission);
            return ExecuteSharedTransfer(binding, transfer, owner, predecessors, site, functionName, token, admission);
        }, site, retainResult: true);
    }

    private async ValueTask<bool> ExecuteSharedTransfer(int binding, HpirPreparedTransfer transfer,
        FileTransfers owner, Task[] predecessors, HpirWorkSite site, string functionName, CancellationToken token,
        FileAdmission admission)
    {
        try
        {
            await AwaitSharedTransfer(admission.Waits, site, token).ConfigureAwait(false);
            return await ExecutePreparedTransfer(binding, transfer, owner, predecessors, site, functionName, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is MsyncException or InvalidOperationException)
        {
            await ReportTransferError(owner, site, error).ConfigureAwait(false);
            return false;
        }
        finally { admission.Release(); }
    }

    private static async ValueTask AwaitSharedTransfer(FileWaits waits, HpirWorkSite site, CancellationToken token)
    {
        if (waits.Tasks.Count == 0) return;
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.cross_wait");
        if (trace is not null)
        {
            trace.SetMetric("dependencies.cross", waits.Cross);
            foreach (Task prior in waits.Tasks) trace.Link("transfer.cross_predecessor", prior);
        }
        try { await AwaitLivePredecessors(waits.Tasks, token).ConfigureAwait(false); }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private static async ValueTask<bool> ExecutePreparedTransfer(int binding, HpirPreparedTransfer transfer,
        FileTransfers owner, Task[] predecessors, HpirWorkSite site, string functionName, CancellationToken token)
    {
        var summary = new TransferSummary(owner.Context.Observe);
        using RuntimeTimelineScope? observation = site.Begin("hpir.effect.transfer");
        if (observation is not null)
        {
            observation.SetAttribute("function.name", functionName);
            observation.SetAttribute("transfer.kind", transfer.Kind.ToString());
            observation.SetAttribute("transfer.destination", transfer.Destination);
            observation.SetMetric("transfer.binding", binding);
            observation.SetMetric("dependencies.prepared", predecessors.Length);
            observation.SetMetric("selection.frozen_members",
                transfer.Kind == HpirTransferSource.Selection && transfer.Pattern is null ? transfer.Paths.Count : 0);
            observation.SetMetric("selection.live", transfer.Pattern is null ? 0 : 1);
            foreach (Task prior in predecessors) observation.Link("transfer.predecessor", prior);
        }
        try
        {
            await AwaitPredecessors(predecessors, token).ConfigureAwait(false);
            await owner.Capacity.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool success = await ExecuteHostTransfer(transfer, owner.Context, token).ConfigureAwait(false);
                if (summary.Enabled) summary.Finish(transfer.Kind == HpirTransferSource.Path ? transfer.Paths[0] : null, transfer.Destination);
                return success;
            }
            finally { owner.Capacity.Release(); }
        }
        catch (Exception error)
        {
            observation?.Fail(error);
            if (summary.Enabled) summary.Finish(transfer.Kind == HpirTransferSource.Path ? transfer.Paths[0] : null, transfer.Destination, error);
            if (error is IOException or UnauthorizedAccessException || error is MsyncException { Span: null })
                throw new MsyncException($"Transfer to '{transfer.Destination}': {error.Message}", site.Source, error);
            throw;
        }
    }

    private readonly struct TransferSummary(Action<RuntimeObservation>? report)
    {
        private readonly long start = report is null ? 0 : Stopwatch.GetTimestamp();
        internal bool Enabled => report is not null;
        internal void Finish(string? source, string destination, Exception? error = null) =>
            report!(new(source is null ? "transfer" : "copy",
                error is null ? "success" : error is OperationCanceledException ? "cancelled" : "failure",
                source, destination, error?.Message, Math.Max(1, (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds)));
    }

    private static async ValueTask AwaitPredecessors(Task[] predecessors, CancellationToken token)
    {
        foreach (Task predecessor in predecessors)
        {
            if (predecessor.IsCompletedSuccessfully) continue;
            try { await predecessor.WaitAsync(token).ConfigureAwait(false); }
            catch (MsyncException) when (!token.IsCancellationRequested)
            {
                // An admitted failed transfer still completes its resource
                // obligation. The owner reports the original failure once.
            }
        }
        token.ThrowIfCancellationRequested();
    }

    private static async ValueTask<bool> ExecuteHostTransfer(HpirPreparedTransfer transfer, NativeRuntimeContext context,
        CancellationToken token)
    {
        NativePathValue destination = new(transfer.Destination);
        ValueTask<bool> copy = transfer.Kind switch
        {
            HpirTransferSource.Path => NativeHostIO.TransferPathAsync(new(transfer.Paths[0]), destination, context, token),
            HpirTransferSource.Text => NativeHostIO.TransferTextAsync(transfer.Text!, destination, context, token),
            HpirTransferSource.Selection when transfer.Pattern is { } pattern =>
                NativeHostIO.TransferSelectedPathsAsync(new(transfer.Paths[0]), pattern, destination, context, token),
            _ => TransferFrozenSelection(transfer.Paths, destination, context, token)
        };
        bool success = await copy.ConfigureAwait(false);
        if (!success) throw new MsyncException($"Transfer to '{transfer.Destination}' failed.", transfer.Source);
        return true;
    }

    private static async ValueTask<bool> TransferFrozenSelection(IReadOnlyList<string> paths, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"Range<Path> -> {destination}");
            return true;
        }
        await NativeHostIO.TransferPathBatchAsync(paths.Select(path => new NativePathValue(path)).ToArray(),
            destination, context, token).ConfigureAwait(false);
        return true;
    }

}
