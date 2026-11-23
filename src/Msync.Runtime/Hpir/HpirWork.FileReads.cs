using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal HpirHostResult<T> SubmitFileMetadata<T>(HpirFileMetadata operation, NativePathValue path,
        ExecutionOptions? options, BindingContext? binding, HpirWorkSite site,
        Func<HpirFileMetadata, NativePathValue, NativeRuntimeContext, CancellationToken, ValueTask<T>> read)
    {
        FileTransfers owner = fileTransfers ??= new(options, binding);
        using RuntimeTimelineScope? admission = site.Begin("hpir.file_read.admit");
        bool remote = path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl };
        bool hasWork = owner.Resources?.HasRegistrations == true;
        Task? ordering = Ordering;
        FileKeys key = remote || hasWork || ordering is not null ? FileKeysFor(path, owner.Context) : default;
        FileWaits? waits = hasWork ? owner.Resources!.BeforeRead(key) : null;
        HashSet<Task>? dependencies = waits?.Tasks;
        if (ordering is not null) AddOrdering(dependencies ??= [], ordering, admission);
        if (admission is not null)
        {
            admission.SetAttribute("file.operation", HpirFileMetadataImports.NativeName(operation));
            admission.SetAttribute("input.resource", path.ToString());
            admission.SetAttribute("input.endpoint", remote ? "remote" : "local");
            admission.SetMetric("dependencies.live", dependencies?.Count ?? 0);
            admission.SetMetric("dependencies.cross", waits?.Cross ?? 0);
            if (dependencies is not null)
                foreach (Task predecessor in dependencies) admission.Link("file_read.predecessor", predecessor);
        }

        if (!remote && (dependencies is null || dependencies.Count == 0))
        {
            // Only the mutator registers work; a synchronously completed
            // local read cannot race with a later VM write admission.
            var result = ReadFileMetadata(operation, path, owner.Context, site, read, executionToken);
            if (result.IsCompletedSuccessfully)
            {
                admission?.SetAttribute("work.outcome", "ready");
                return new(0, result.Result);
            }
            admission?.SetAttribute("work.outcome", "deferred-failure");
            return SubmitFinishedMetadata(result, site);
        }

        var delayed = SubmitPendingMetadata(owner, operation, path, key, site, admission, dependencies, read);
        admission?.SetAttribute("work.outcome", delayed.Ready ? "ready" : "deferred");
        return delayed;
    }

    private HpirHostResult<T> SubmitFinishedMetadata<T>(ValueTask<T> result, HpirWorkSite site) =>
        Submit<T>(HpirWorkKind.FileRead, _ => result, site);

    private HpirHostResult<T> SubmitPendingMetadata<T>(FileTransfers owner, HpirFileMetadata operation, NativePathValue path,
        FileKeys key, HpirWorkSite site, RuntimeTimelineScope? admission, HashSet<Task>? dependencies,
        Func<HpirFileMetadata, NativePathValue, NativeRuntimeContext, CancellationToken, ValueTask<T>> read) =>
        SubmitLeasedRead(owner.NeedResources(), key, site, admission, dependencies,
            (_, token) => ReadFileMetadata(operation, path, owner.Context, site, read, token));

    internal HpirHostResult<string?> SubmitFileText(NativePathValue path, ExecutionOptions? options,
        BindingContext? binding, HpirWorkSite site)
    {
        FileTransfers owner = fileTransfers ??= new(options, binding);
        FileResources frontier = owner.NeedResources();
        FileKeys key = FileKeysFor(path, owner.Context);
        FileWaits waits = frontier.BeforeRead(key);
        using RuntimeTimelineScope? admission = site.Begin("hpir.file_read.admit");
        AddOrdering(waits.Tasks, Ordering, admission);
        if (admission is not null)
        {
            admission.SetAttribute("file.operation", "io.read_text");
            admission.SetAttribute("input.resource", path.ToString());
            admission.SetAttribute("input.endpoint", path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } ? "remote" : "local");
            admission.SetMetric("dependencies.live", waits.Tasks.Count);
            admission.SetMetric("dependencies.cross", waits.Cross);
            foreach (Task predecessor in waits.Tasks) admission.Link("file_read.predecessor", predecessor);
        }
        var result = SubmitLeasedRead<string?>(frontier, key, site, admission, waits.Tasks,
            (release, token) => new(Task.Run(() => ReadFileText(path, owner.Context, site, release, token))));
        admission?.SetAttribute("work.outcome", result.Ready ? "ready" : "deferred");
        return result;
    }

    private static async Task<string?> ReadFileText(NativePathValue path, NativeRuntimeContext context,
        HpirWorkSite site, Action release, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_read.read");
        trace?.SetAttribute("file.operation", "io.read_text");
        try
        {
            string text = await NativeBuiltins.ReadFileText(path, context, token, kind =>
            {
                bool coStart = kind is "fifo" or "character" or "socket";
                trace?.SetAttribute("resource.kind", kind);
                trace?.SetMetric("stream.co_start", coStart ? 1 : 0);
                // A matching writer must be able to start a genuine stream.
                if (coStart) release();
            }).ConfigureAwait(false);
            trace?.SetMetric("text.characters", text.Length);
            return text;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            if (error is OperationCanceledException or MsyncException { Span: not null }) throw;
            throw new MsyncException(error.Message, site.Source, error);
        }
    }

    private HpirHostResult<T> SubmitLeasedRead<T>(FileResources frontier, FileKeys key,
        HpirWorkSite site, RuntimeTimelineScope? admission, HashSet<Task>? dependencies,
        Func<Action, CancellationToken, ValueTask<T>> read) =>
        Submit<T>(HpirWorkKind.FileRead, async token =>
        {
            FileLease lease = frontier.AdmitRead(key);
            bool released = false;
            try
            {
                if (admission is not null) site.Timeline!.Register(lease.Completion, admission);
                if (dependencies is not null) await AwaitLivePredecessors(dependencies, token).ConfigureAwait(false);
                return await read(Release, token).ConfigureAwait(false);
            }
            finally { Release(); }

            void Release()
            {
                if (released) return;
                released = true;
                frontier.Release(lease);
            }
        }, site);

    private static async ValueTask<T> ReadFileMetadata<T>(HpirFileMetadata operation, NativePathValue path,
        NativeRuntimeContext context, HpirWorkSite site,
        Func<HpirFileMetadata, NativePathValue, NativeRuntimeContext, CancellationToken, ValueTask<T>> read,
        CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_read.read");
        trace?.SetAttribute("file.operation", HpirFileMetadataImports.NativeName(operation));
        try
        {
            token.ThrowIfCancellationRequested();
            return await read(operation, path, context, token).ConfigureAwait(false);
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }
}
