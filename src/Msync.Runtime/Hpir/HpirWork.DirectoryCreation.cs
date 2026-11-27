using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal bool CreateDirectories(NativePathValue path, ExecutionOptions? options, BindingContext? binding,
        HpirWorkSite site, CancellationToken token)
    {
        FileTransfers owner = Files(options, binding);
        Task? ordering = Ordering;
        var result = Submit<bool>(HpirWorkKind.FileWrite,
            cancellation => CreateDirectories(owner, path, ordering, site, cancellation), site);
        if (result.Ready) return result.Value;
        using RuntimeTimelineScope? trace = site.Begin("hpir.directory_create.observe");
        return Demand<bool>(result.JobId, token, trace);
    }

    private static async ValueTask<bool> CreateDirectories(FileTransfers owner, NativePathValue path,
        Task? ordering, HpirWorkSite site, CancellationToken token)
    {
        const string operation = HpirDirectoryCreationImports.Name;
        using RuntimeTimelineScope? trace = site.Begin("hpir.directory_create.apply");
        if (trace is not null)
        {
            trace.SetAttribute("file.operation", operation);
            trace.SetAttribute("file.destination", path.ToString());
            trace.SetAttribute("file.endpoint", path.Connection?.ToString() ?? "local");
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var frontier = owner.NeedResources();
            var admission = frontier.Admit(null, FileKeysFor(path, owner.Context), sharedTransfer: false);
            try
            {
                await AwaitFileWrite(admission, path, operation, ordering, site, token).ConfigureAwait(false);
                return await ApplyDirectoryCreation(path, owner.Context, trace, site, token).ConfigureAwait(false);
            }
            finally { admission.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var failure = new MsyncException($"{operation} for '{path}': {error.Message}", site.Source, error)
                { DiagnosticCode = "E0300" };
            trace?.Fail(failure);
            throw failure;
        }
        catch (OperationCanceledException error) { trace?.Fail(error); throw; }
    }

    private static async ValueTask<bool> ApplyDirectoryCreation(NativePathValue path, NativeRuntimeContext context,
        RuntimeTimelineScope? operation, HpirWorkSite site, CancellationToken token)
    {
        using RuntimeTimelineScope? host = site.Begin("hpir.directory_create.host");
        try
        {
            bool created = await NativeBuiltins.CreateDirectories(path, context, token).ConfigureAwait(false);
            string outcome = context.DryRun ? "simulated" : created ? "created" : "existing";
            operation?.SetAttribute("file.outcome", outcome);
            host?.SetAttribute("file.outcome", outcome);
            return created;
        }
        catch (Exception error) { host?.Fail(error); throw; }
    }
}
