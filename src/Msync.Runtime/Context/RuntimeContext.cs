using Msync.Core;

namespace Msync.Runtime;

public sealed class RuntimeContext
{
    public string WorkingDirectory { get; private set; }
    public string HomeDirectory { get; }
    public string? SourceFile { get; }
    public bool DryRun { get; }
    public TextReader Input => input ?? Console.In;
    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public int Jobs { get; }
    public int BufferSize { get; }
    public long MemoryLimit { get; }
    public long? SpoolLimit { get; }
    public Action<RuntimeObservation>? Observe { get; }
    public Action<MsyncException>? ReportError { get; }
    private readonly TextReader? input;
    private readonly Action<string>? workingDirectoryChanged;
    private RuntimeContext? directoryCapture;
    public RuntimeContext(ExecutionOptions? options = null, BindingContext? binding = null)
    {
        options ??= new();
        WorkingDirectory = binding?.WorkingDirectory ?? Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        HomeDirectory = binding?.HomeDirectory ?? HostContext.HomeDirectory;
        SourceFile = binding?.SourceFile ?? options.SourceFile;
        DryRun = options.DryRun;
        input = options.Input;
        Output = options.Output ?? Console.Out;
        Error = options.Error ?? Console.Error;
        ReportError = options.ReportError;
        workingDirectoryChanged = options.WorkingDirectoryChanged;
        Jobs = options.Jobs;
        BufferSize = options.BufferSize; MemoryLimit = options.MemoryLimit; SpoolLimit = options.SpoolLimit; Observe = options.Observe;
    }
    private RuntimeContext(RuntimeContext source)
    {
        WorkingDirectory = source.WorkingDirectory;
        HomeDirectory = source.HomeDirectory;
        SourceFile = source.SourceFile;
        DryRun = source.DryRun;
        input = source.input;
        Output = source.Output;
        Error = source.Error;
        ReportError = source.ReportError;
        Jobs = source.Jobs;
        BufferSize = source.BufferSize;
        MemoryLimit = source.MemoryLimit;
        SpoolLimit = source.SpoolLimit;
        Observe = source.Observe;
        directoryCapture = this;
    }
    // Deferred work retains its admission's directory, not later cd state.
    internal RuntimeContext CaptureDirectory() => directoryCapture ??= new(this);
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
            throw new MsyncException("cd changes the local execution directory; qualify remote command paths with 'connection @ path'.");
        string directory = Path.GetFullPath(LocalPath(path));
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            throw new IOException($"Cannot cd to '{directory}': it is not a directory.");
        NativeAccess.SearchDirectory(directory);
        WorkingDirectory = directory;
        directoryCapture = null;
        workingDirectoryChanged?.Invoke(directory);
        return new(directory, directory, HomeDirectory: HomeDirectory);
    }
}
