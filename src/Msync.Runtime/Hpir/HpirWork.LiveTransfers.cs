using Msync.Core;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private int failedTransfers;
    internal int FailedTransfers => Volatile.Read(ref failedTransfers);

    internal HpirHostResult<byte> SubmitLiveTransfer(NativePathValue? source, HpirTransferTextInput text, NativePathValue destination,
        ExecutionOptions? options, BindingContext? binding, HpirWorkSite site, string? selection = null)
    {
        FileTransfers owner = Files(options, binding);
        FileResources frontier = owner.NeedResources();
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.admit");
        FileKeys? read = source is { } input ? FileKeysFor(input, owner.Context) : null;
        FileKeys write = FileKeysFor(destination, owner.Context);
        if (trace is not null)
        {
            trace.SetAttribute("transfer.destination", destination.ToString());
            trace.SetAttribute("transfer.kind", selection is not null ? "selection" : source.HasValue ? "path" : "text");
            trace.SetMetric("resources.reads", read.HasValue ? 1 : 0);
            if (selection is not null) trace.SetMetric("selection.live", 1);
            if (source is { } path) trace.SetAttribute("transfer.source", path.ToString());
            if (text.Dependency is { } content)
            {
                trace.SetMetric("input.text_pending", content.IsCompleted ? 0 : 1);
                trace.Link(text.Number is null ? "transfer.text_input" : "transfer.number_input", content);
            }
        }

        Task? ordering = Ordering;
        return Submit<byte>(HpirWorkKind.Transfer, token =>
        {
            FileAdmission admission = frontier.Admit(read, write, sharedTransfer: false);
            AddOrdering(admission.Waits.Tasks, ordering, trace);
            ReportAdmission(site, trace, admission);
            return LiveTransfer(owner, source, text, destination, site, admission, selection, token);
        }, site, processDependency: text.Process);
    }

    internal Task TransferCompletion(int id) => jobs[id].Completion;

    internal void TrackTransfer(int id, nint frame, nint cell, HpirHeap? heap)
    {
        if (id == 0) return;
        Attach(jobs[id], frame, cell);
        if (cell != 0) heap!.TrackWork(this);
    }

    private async ValueTask AwaitLiveRead(NativePathValue path, HpirWorkSite site, Task? ordering, CancellationToken token)
    {
        if (fileTransfers is not { Resources: { } frontier } owner)
        {
            if (ordering is not null) await ordering.WaitAsync(token).ConfigureAwait(false);
            return;
        }
        FileWaits waits = frontier.BeforeRead(FileKeysFor(path, owner.Context));
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.read_dependencies");
        AddOrdering(waits.Tasks, ordering, trace);
        if (trace is not null)
        {
            trace.SetAttribute("input.resource", path.ToString());
            trace.SetMetric("dependencies.live", waits.Tasks.Count);
            trace.SetMetric("dependencies.cross", waits.Cross);
            foreach (Task predecessor in waits.Tasks) trace.Link("transfer.predecessor", predecessor);
        }
        try
        {
            await AwaitLivePredecessors(waits.Tasks, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private async ValueTask<byte> LiveTransfer(FileTransfers owner, NativePathValue? source, HpirTransferTextInput text,
        NativePathValue destination, HpirWorkSite site, FileAdmission admission, string? selection, CancellationToken token)
    {
        var summary = new TransferSummary(owner.Context.Observe);
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.transfer");
        if (trace is not null)
        {
            trace.SetAttribute("transfer.destination", destination.ToString());
            trace.SetMetric("dependencies.received", admission.Waits.Tasks.Count);
            foreach (Task predecessor in admission.Waits.Tasks) trace.Link("transfer.predecessor", predecessor);
        }
        try
        {
            await AwaitLivePredecessors(admission.Waits.Tasks, token).ConfigureAwait(false);
            string? contents = null;
            if (source is null)
            {
                if (trace is not null && text.Dependency is { } input)
                {
                    trace.Link(text.Number is null ? "transfer.text_input" : "transfer.number_input", input);
                    trace.SetMetric("input.text_suspended", input.IsCompleted ? 0 : 1);
                }
                contents = await text.Read(site, token).ConfigureAwait(false);
                trace?.SetMetric("input.text_characters", contents.Length);
            }
            await owner.Capacity.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool success = source is { } path
                    ? selection is null
                        ? await NativeHostIO.TransferPathAsync(path, destination, owner.Context, token).ConfigureAwait(false)
                        : await NativeHostIO.TransferSelectedPathsAsync(path, selection, destination, owner.Context, token).ConfigureAwait(false)
                    : await NativeHostIO.TransferTextAsync(contents!, destination, owner.Context, token).ConfigureAwait(false);
                if (summary.Enabled)
                    summary.Finish(source?.ToString(), destination.ToString(), success ? null : new MsyncException("Transfer did not complete.", site.Source));
                if (success) return 1;
                Interlocked.Increment(ref failedTransfers);
                return 0;
            }
            finally { owner.Capacity.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException or InvalidOperationException)
        {
            trace?.Fail(error);
            if (summary.Enabled) summary.Finish(source?.ToString(), destination.ToString(), error);
            await ReportTransferError(owner, site, error).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure)
        {
            trace?.Fail(failure);
            if (summary.Enabled) summary.Finish(source?.ToString(), destination.ToString(), failure);
            throw;
        }
        finally { admission.Release(); }
    }

    private static async ValueTask AwaitLivePredecessors(HashSet<Task> dependencies, CancellationToken token)
    {
        foreach (Task predecessor in dependencies)
            if (!predecessor.IsCompletedSuccessfully) await predecessor.WaitAsync(token).ConfigureAwait(false);
    }

    private static void ReportAdmission(HpirWorkSite site, RuntimeTimelineScope? trace, FileAdmission admission)
    {
        if (trace is null) return;
        try
        {
            trace.SetMetric("dependencies.live", admission.Waits.Tasks.Count);
            trace.SetMetric("dependencies.cross", admission.Waits.Cross);
            foreach (Task predecessor in admission.Waits.Tasks) trace.Link("transfer.predecessor", predecessor);
            site.Timeline!.Register(admission.Lease.Completion, trace);
        }
        catch { admission.Release(); throw; }
    }

    private async ValueTask ReportTransferError(FileTransfers owner, HpirWorkSite site, Exception error)
    {
        Interlocked.Increment(ref failedTransfers);
        var diagnostic = error as MsyncException is { Span: not null } attributed ? attributed : new MsyncException(error.Message, site.Source, error);
        if (owner.Context.ReportError is { } report) report(diagnostic);
        else await owner.Context.Error.WriteLineAsync($"msync: {diagnostic.Message}").ConfigureAwait(false);
    }
}
