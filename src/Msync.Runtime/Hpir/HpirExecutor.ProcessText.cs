using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private readonly record struct PendingProcessText(int Index, HpirTransferTextInput Text);

    private static int AdmitProcessWithText(HpirWork work, NativePathValue path, string[] argv,
        List<PendingProcessText> pending, NativeRuntimeContext context, bool release, HpirWorkSite site) =>
        work.SubmitProcess(token => release
            ? new(Task.Run(() => StartProcessWithText(path, argv, pending, context, site, token).AsTask()))
            : StartProcessWithText(path, argv, pending, context, site, token), site);
}

// Only the host arguments below are retained while argv is pending.
internal static partial class HpirExecutor
{
    private static async ValueTask<NativeProcess> StartProcessWithText(NativePathValue path, string[] argv,
        List<PendingProcessText> pending, NativeRuntimeContext context, HpirWorkSite site, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.process.pending_argv");
        trace?.SetMetric("arguments.pending_text", pending.Count);
        int suspended = 0;
        try
        {
            foreach (var (index, input) in pending)
            {
                if (input.Dependency is { } dependency)
                {
                    if (!dependency.IsCompleted) ++suspended;
                    trace?.Link("process.argument_text", dependency);
                }
                argv[index] = await input.Read(site, token, "A process argument must contain String, not Null.")
                    .ConfigureAwait(false);
            }
            return await NativeHostIO.StartTextProcessAsync(path, argv, context, pipeInput: false, token).ConfigureAwait(false);
        }
        catch (Exception error) { trace?.Fail(error); throw; }
        finally { trace?.SetMetric("arguments.suspended", suspended); }
    }
}
