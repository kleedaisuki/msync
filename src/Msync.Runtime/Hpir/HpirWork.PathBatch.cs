using Msync.Core;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal HpirHostResult<byte> SubmitPathBatch(HpirPathBatchInput input, NativePathValue destination, bool unknownReads,
        ExecutionOptions? options, BindingContext? binding, HpirWorkSite site, out Task? earliest)
    {
        FileTransfers owner = Files(options, binding);
        FileResources frontier = owner.NeedResources();
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.path_batch_admit");
        trace?.SetAttribute("transfer.destination", destination.ToString());
        trace?.SetMetric("source.paths_dynamic", unknownReads ? 1 : 0);
        var write = FileKeysFor(destination, owner.Context);
        Task? ordering = Ordering, prerequisites = null;
        var result = Submit<byte>(HpirWorkKind.Transfer, token =>
        {
            // Callback results can name any source. Match the existing
            // unknown-cohort frontier; never claim an individual member early.
            FileKeys? read = unknownReads ? new(new("*", "")) : null;
            FileAdmission admission = frontier.Admit(read, write, sharedTransfer: false);
            AddOrdering(admission.Waits.Tasks, ordering, trace);
            ReportAdmission(site, trace, admission);
            if (admission.Waits.Tasks.Count != 0) prerequisites = Task.WhenAll(admission.Waits.Tasks);
            return RunPathBatch(owner, input, destination, site, admission, prerequisites, token);
        }, site);
        earliest = prerequisites;
        return result;
    }

    private async ValueTask<byte> RunPathBatch(FileTransfers owner, HpirPathBatchInput input, NativePathValue destination,
        HpirWorkSite site, FileAdmission admission, Task? prerequisites, CancellationToken token)
    {
        var summary = new TransferSummary(owner.Context.Observe);
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.path_batch");
        try
        {
            if (prerequisites is not null) await prerequisites.WaitAsync(token).ConfigureAwait(false);
            trace?.Link("guest.path_delivery", input.Paths.Task);
            NativePathValue[] paths = await input.Paths.Task.WaitAsync(token).ConfigureAwait(false);
            trace?.SetMetric("transfer.path_items", paths.Length);
            await owner.Capacity.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await NativeHostIO.TransferPathBatchAsync(paths, destination, owner.Context, token).ConfigureAwait(false);
                if (summary.Enabled) summary.Finish(null, destination.ToString());
                return 1;
            }
            finally { owner.Capacity.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException or InvalidOperationException)
        {
            trace?.Fail(error);
            if (summary.Enabled) summary.Finish(null, destination.ToString(), error);
            await ReportTransferError(owner, site, error).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            if (summary.Enabled) summary.Finish(null, destination.ToString(), error);
            throw;
        }
        finally { admission.Release(); }
    }
}
