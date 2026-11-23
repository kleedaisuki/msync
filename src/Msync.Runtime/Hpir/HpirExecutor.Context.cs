using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static partial class HpirExecutor
{
    private static bool ExecuteContextCheck(HpirContextCheck check, BindingContext? context,
        ExecutionOptions? options, CancellationToken token)
    {
        if (check.Value is { } value) return value;
        var host = new NativeRuntimeContext(options, context);
        var path = new Msync.Core.Native.NativePath(check.Path.Raw,
            LocalSpelling: OperatingSystem.IsWindows() ? check.Path.WindowsSpelling : null);
        using RuntimeTimelineScope? trace = options?.Timeline?.Begin("hpir.effect.context_check");
        trace?.SetSource(check.Source);
        trace?.SetAttribute("input.path", check.Path.Raw);
        trace?.SetAttribute("operation", HpirFileMetadataImports.NativeName(check.Operation));
        try
        {
            return NativeBuiltins.ReadFilePredicate(check.Operation, path, host, token).GetAwaiter().GetResult() != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            trace?.Fail(error);
            throw new MsyncException(error.Message, check.Source, error);
        }
    }
}
