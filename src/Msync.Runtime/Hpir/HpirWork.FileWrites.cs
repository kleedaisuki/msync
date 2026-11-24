using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal void WriteFileText(NativePathValue destination, HpirTransferTextInput input, bool append,
        ExecutionOptions? options, BindingContext? binding, HpirWorkSite site, CancellationToken token)
    {
        FileTransfers owner = Files(options, binding);
        Task? ordering = Ordering;
        var result = Submit<bool>(HpirWorkKind.FileWrite,
            cancellation => WriteFileText(owner, destination, input, append, ordering, site, cancellation), site);
        if (result.Ready) return;
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_write.observe");
        _ = Demand<bool>(result.JobId, token, trace);
    }

    private static async ValueTask<bool> WriteFileText(FileTransfers owner, NativePathValue destination,
        HpirTransferTextInput input, bool append, Task? ordering, HpirWorkSite site, CancellationToken token)
    {
        try
        {
            // Explicit writes observe their arguments before reserving the
            // destination. A transfer arrow instead reserves it on admission.
            string contents = await FileWriteContent(input, site, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            FileResources frontier = owner.NeedResources();
            FileAdmission admission = frontier.Admit(null, FileKeysFor(destination, owner.Context), sharedTransfer: false);
            try
            {
                await AwaitFileWrite(admission, destination, HpirFileTextWriteImports.NativeName(append), ordering, site, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using RuntimeTimelineScope? write = site.Begin("hpir.file_write.write");
                write?.SetAttribute("file.operation", HpirFileTextWriteImports.NativeName(append));
                write?.SetAttribute("file.action", owner.Context.DryRun ? "simulate" : "write");
                write?.SetMetric("text.characters", contents.Length);
                try { await NativeBuiltins.WriteFileText(destination, contents, append, owner.Context, token).ConfigureAwait(false); }
                catch (Exception error) { write?.Fail(error); throw; }
                return true;
            }
            finally { admission.Release(); }
        }
        catch (Exception error) when (error is not (OperationCanceledException or MsyncException { Span: not null }))
        {
            throw new MsyncException(error.Message, site.Source, error);
        }
    }

    private static async ValueTask<string> FileWriteContent(HpirTransferTextInput input, HpirWorkSite site, CancellationToken token)
    {
        if (input.Dependency is not { } pending) return await input.Read(site, token).ConfigureAwait(false);
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_write.arguments");
        if (trace is not null)
        {
            trace.Link("file_write.text_input", pending);
            trace.SetMetric("input.text_pending", pending.IsCompleted ? 0 : 1);
        }
        try { return await input.Read(site, token).ConfigureAwait(false); }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private static async ValueTask AwaitFileWrite(FileAdmission admission, NativePathValue destination,
        string operation, Task? ordering, HpirWorkSite site, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.file_write.admit");
        AddOrdering(admission.Waits.Tasks, ordering, trace);
        if (trace is not null)
        {
            trace.SetAttribute("file.destination", destination.ToString());
            trace.SetAttribute("file.operation", operation);
            trace.SetMetric("dependencies.live", admission.Waits.Tasks.Count);
            trace.SetMetric("dependencies.cross", admission.Waits.Cross);
            foreach (Task prior in admission.Waits.Tasks) trace.Link("file_write.predecessor", prior);
            site.Timeline!.Register(admission.Lease.Completion, trace);
        }
        try { await AwaitLivePredecessors(admission.Waits.Tasks, token).ConfigureAwait(false); }
        catch (Exception error) { trace?.Fail(error); throw; }
    }
}
