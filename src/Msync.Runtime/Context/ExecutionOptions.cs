using Msync.Core;

namespace Msync.Runtime;

public sealed record ExecutionOptions(string? WorkingDirectory = null, string? SourceFile = null, bool DryRun = false,
    TextWriter? Output = null, TextWriter? Error = null, int Jobs = 4, int BufferSize = 65536, long MemoryLimit = 67108864,
    long? SpoolLimit = null, Action<RuntimeObservation>? Observe = null)
{
    public TextReader? Input { get; init; }
    public Action<MsyncException>? ReportError { get; init; }
    public Action<string>? WorkingDirectoryChanged { get; init; }
}
