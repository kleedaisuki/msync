using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    internal static async ValueTask<bool> TransferSelectedPathsAsync(NativePathValue source, string pattern,
        NativePathValue destination, NativeRuntimeContext context, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = context.Timeline?.Begin("file.selection.transfer");
        if (trace is not null)
        {
            trace.SetAttribute("selection.directory", source.ToString());
            trace.SetAttribute("transfer.destination", destination.ToString());
            trace.SetMetric("regex.pattern_code_units", pattern.Length);
        }
        try
        {
            if (context.DryRun)
            {
                await context.Output.WriteLineAsync($"Range<Path> -> {destination}");
                return true;
            }
            if (Remote(source).HasValue)
                return await TransferSelectionAsync(source, pattern, destination, context, token).ConfigureAwait(false);
            List<NativePathValue> paths = [];
            await foreach (var path in OpenSelectedPaths(source, pattern, context.CaptureDirectory(), token).ConfigureAwait(false))
                paths.Add(path);
            await TransferPathBatchAsync(paths.ToArray(), destination, context, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    internal static async ValueTask<bool> TransferPathAsync(NativePathValue source, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"{source} -> {destination}");
            return true;
        }
        bool container = EndsInSeparator(destination), replace = IsReplacement(destination);
        var sourceRemote = Remote(source);
        var targetRemote = Remote(destination);
        if (targetRemote is { } endpoint)
        {
            if (sourceRemote == endpoint)
                await CopyRemoteAsync([source], destination, context, container, replace, false, token);
            else if (sourceRemote is null)
                await UploadPayloadAsync([source], null, destination, context, container, replace, false, token);
            else await RelayPathsAsync([source], destination, context, container, replace, false, token);
            return true;
        }
        if (sourceRemote.HasValue)
            await DownloadAsync([source], destination, context, container, replace, token);
        else
            await CopyLocalPathAsync(source, PhysicalTargetPath(destination, source, context, container), context, container, replace, token);
        return true;
    }

    internal static async ValueTask<bool> TransferTextAsync(string text, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"{text} -> {destination}");
            return true;
        }
        if (EndsInSeparator(destination))
            throw new MsyncException("A container transfer requires Path sources with basenames; a String source is literal data.");
        if (Remote(destination).HasValue)
            await UploadPayloadAsync(null, text, destination, context, false, IsReplacement(destination), false, token);
        else
        {
            string target = PhysicalTargetPath(destination, null, context, container: false);
            await TransferFiles.WriteLiteralAsync(text, target, token, context.BufferSize, context.Timeline);
        }
        return true;
    }
}
