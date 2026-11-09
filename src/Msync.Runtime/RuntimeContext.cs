using Msync.Core;

namespace Msync.Runtime;

public sealed record ExecutionOptions(string? WorkingDirectory = null, string? SourceFile = null, bool DryRun = false,
    TextWriter? Output = null, TextWriter? Error = null, int Jobs = 4, int BufferSize = 65536, long MemoryLimit = 67108864,
    long? SpoolLimit = null, Action<RuntimeObservation>? Observe = null)
{
    public Action<MsyncException>? ReportError { get; init; }
    public Action<string>? WorkingDirectoryChanged { get; init; }
}

public sealed record RuntimeObservation(string Phase, string Outcome, string? Source = null, string? Destination = null, string? Detail = null, long DurationNanoseconds = 0);

public sealed class RuntimeContext
{
    public string WorkingDirectory { get; private set; }
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
    public Action<MsyncException>? ReportError { get; }
    private readonly Action<string>? workingDirectoryChanged;
    public RuntimeContext(ExecutionOptions? options = null, BindingContext? binding = null)
    {
        options ??= new();
        WorkingDirectory = binding?.WorkingDirectory ?? Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        HomeDirectory = binding?.HomeDirectory ?? HostContext.HomeDirectory;
        SourceFile = binding?.SourceFile ?? options.SourceFile;
        DryRun = options.DryRun;
        Output = options.Output ?? Console.Out;
        Error = options.Error ?? Console.Error;
        ReportError = options.ReportError;
        workingDirectoryChanged = options.WorkingDirectoryChanged;
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

    public PathValue ChangeWorkingDirectory(Value value)
    {
        PathValue path = AsPath(value);
        if (path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            throw new MsyncException("cd changes the local execution directory; qualify remote command paths with 'path @ connection'.");
        string directory = Path.GetFullPath(LocalPath(path));
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            throw new IOException($"Cannot cd to '{directory}': it is not a directory.");
        WorkingDirectory = directory;
        workingDirectoryChanged?.Invoke(directory);
        return new(directory, directory, HomeDirectory: HomeDirectory);
    }
}
