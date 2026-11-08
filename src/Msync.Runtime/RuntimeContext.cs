using Msync.Core;

namespace Msync.Runtime;

public sealed record ExecutionOptions(string? WorkingDirectory = null, string? SourceFile = null, bool DryRun = false,
    TextWriter? Output = null, TextWriter? Error = null, int Jobs = 4, int BufferSize = 65536, long MemoryLimit = 67108864,
    long? SpoolLimit = null, Action<RuntimeObservation>? Observe = null);

public sealed record RuntimeObservation(string Phase, string Outcome, string? Source = null, string? Destination = null, string? Detail = null, long DurationNanoseconds = 0);

public sealed class RuntimeContext
{
    public string WorkingDirectory { get; }
    public string HomeDirectory { get; }
    public string? SourceFile { get; }
    public bool DryRun { get; }
    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public int Jobs { get; }
    public int BufferSize { get; }
    public long MemoryLimit { get; }
    public long? SpoolLimit { get; }
    public Action<RuntimeObservation>? Observe { get; }
    public RuntimeContext(ExecutionOptions? options = null, BindingContext? binding = null)
    {
        options ??= new();
        WorkingDirectory = binding?.WorkingDirectory ?? Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        HomeDirectory = binding?.HomeDirectory ?? HostContext.HomeDirectory;
        SourceFile = binding?.SourceFile ?? options.SourceFile;
        DryRun = options.DryRun;
        Output = options.Output ?? Console.Out;
        Error = options.Error ?? Console.Error;
        Jobs = options.Jobs;
        BufferSize = options.BufferSize; MemoryLimit = options.MemoryLimit; SpoolLimit = options.SpoolLimit; Observe = options.Observe;
    }
    public PathValue AsPath(Value value) => value switch
    {
        PathValue path => path,
        StringValue text => new(text.Data, WorkingDirectory, HomeDirectory: HomeDirectory),
        _ => throw new MsyncException($"Expected Path or String, received {value.Type}")
    };
    public string LocalPath(Value value) => AsPath(value).LocalPath(WorkingDirectory, HomeDirectory);
}
