using Msync.Core;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal void RenameFile(NativePathValue source, NativePathValue destination, ExecutionOptions? options,
        BindingContext? binding, HpirWorkSite site, CancellationToken token)
    {
        FileTransfers owner = Files(options, binding);
        Task? ordering = Ordering;
        var result = Submit<bool>(HpirWorkKind.FileWrite,
            cancellation => RenameFile(owner, source, destination, ordering, site, cancellation), site);
        if (result.Ready) return;
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_rename.observe");
        _ = Demand<bool>(result.JobId, token, trace);
    }

    private static async ValueTask<bool> RenameFile(FileTransfers owner, NativePathValue source, NativePathValue destination,
        Task? ordering, HpirWorkSite site, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            NativeFileRename rename = NativeBuiltins.SelectFileRename(source, destination, owner.Context);
            FileResources frontier = owner.NeedResources();
            FileKeys from, to;
            if (rename.Endpoint is { } endpoint) from = to = new(new(endpoint.ToString(), ""));
            else { from = FileKeysFor(rename.Source); to = FileKeysFor(rename.Destination); }
            FileRenameAdmission admission = frontier.AdmitRename(from, to);
            try
            {
                await AwaitRename(rename, admission, ordering, site, token).ConfigureAwait(false);
                await ApplyRename(rename, owner.Context, site, token).ConfigureAwait(false);
                return true;
            }
            finally { frontier.ReleaseRename(admission); }
        }
        catch (Exception error) when (error is not (OperationCanceledException or MsyncException { Span: not null }))
        {
            throw new MsyncException(error.Message, site.Source, error);
        }
    }

    private static async ValueTask AwaitRename(NativeFileRename rename, FileRenameAdmission admission,
        Task? ordering, HpirWorkSite site, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_rename.admit");
        AddOrdering(admission.Waits.Tasks, ordering, trace);
        if (trace is not null)
        {
            DescribeRename(trace, rename);
            trace.SetMetric("resources.writes", admission.Source.Count + admission.Destination.Count);
            trace.SetMetric("dependencies.live", admission.Waits.Tasks.Count);
            trace.SetMetric("dependencies.cross", admission.Waits.Cross);
            foreach (Task prior in admission.Waits.Tasks) trace.Link("file_rename.predecessor", prior);
            site.Timeline!.Register(admission.Signal.Task, trace);
        }
        try { await AwaitLivePredecessors(admission.Waits.Tasks, token).ConfigureAwait(false); }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private static async ValueTask ApplyRename(NativeFileRename rename, NativeRuntimeContext context,
        HpirWorkSite site, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_rename.rename");
        if (trace is not null)
        {
            DescribeRename(trace, rename);
            trace.SetAttribute("file.action", context.DryRun ? "simulate" : "rename");
        }
        try { await NativeBuiltins.RenameFile(rename, context, token).ConfigureAwait(false); }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private static void DescribeRename(RuntimeTimelineScope trace, NativeFileRename rename)
    {
        trace.SetAttribute("file.operation", "fs.rename");
        trace.SetAttribute("file.source", rename.Source);
        trace.SetAttribute("file.destination", rename.Destination);
        trace.SetAttribute("file.endpoint", rename.Endpoint?.ToString() ?? "local");
    }

    private readonly record struct FileRenameAdmission(TaskCompletionSource Signal, RenameWrites Source,
        RenameWrites Destination, FileWaits Waits);

    private readonly record struct RenameWrites(ResourceIndex.Registration Primary, ResourceIndex.Registration Alias)
    {
        internal int Count => (Primary.Writer is null ? 0 : 1) + (Alias.Writer is null ? 0 : 1);

        internal static RenameWrites Register(ResourceIndex index, Task completion, FileKeys keys) =>
            new(index.Add(completion, null, keys.Primary),
                keys.Alias is { } alias ? index.Add(completion, null, alias) : default);

        internal void Release(ResourceIndex index, Task completion)
        {
            index.Remove(completion, Primary);
            index.Remove(completion, Alias);
        }
    }

    private sealed partial class FileResources
    {
        internal FileRenameAdmission AdmitRename(FileKeys source, FileKeys destination)
        {
            HashSet<Task> dependencies = [];
            bool distinct = source != destination;
            lock (gate)
            {
                source.Collect(residual, true, dependencies);
                if (distinct) destination.Collect(residual, true, dependencies);
                int residualCount = dependencies.Count;
                if (prepared is { } other)
                {
                    source.Collect(other, true, dependencies);
                    if (distinct) destination.Collect(other, true, dependencies);
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var from = RenameWrites.Register(residual, signal.Task, source);
                var to = distinct ? RenameWrites.Register(residual, signal.Task, destination) : default;
                ++activeLeases;
                return new(signal, from, to, new(dependencies, dependencies.Count - residualCount));
            }
        }

        internal void ReleaseRename(FileRenameAdmission admission)
        {
            lock (gate)
            {
                admission.Source.Release(residual, admission.Signal.Task);
                admission.Destination.Release(residual, admission.Signal.Task);
                --activeLeases;
            }
            admission.Signal.TrySetResult();
        }
    }
}
