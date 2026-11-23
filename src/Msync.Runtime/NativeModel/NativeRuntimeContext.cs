using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public sealed class NativeRuntimeContext
{
    internal NativeConnections Connections { get; }
    private readonly Action<string>? directoryChanged;
    private NativeRuntimeContext? captured;
    private readonly NativeRuntimeContext? directoryAuthority;

    public NativeRuntimeContext(ExecutionOptions? options = null, BindingContext? binding = null)
    {
        options ??= new();
        WorkingDirectory = binding?.WorkingDirectory ?? Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        HomeDirectory = binding?.HomeDirectory ?? HostContext.HomeDirectory;
        SourceFile = binding?.SourceFile ?? options.SourceFile;
        Output = options.Output ?? Console.Out;
        Error = options.Error ?? Console.Error;
        DryRun = options.DryRun;
        BufferSize = options.BufferSize;
        SpoolLimit = options.SpoolLimit;
        Timeline = options.Timeline;
        Connections = new(Timeline);
        Observe = options.Observe;
        ReportError = options.ReportError;
        directoryChanged = options.WorkingDirectoryChanged;
    }

    private NativeRuntimeContext(NativeRuntimeContext source)
    {
        Connections = source.Connections;
        directoryAuthority = source;
        WorkingDirectory = source.WorkingDirectory;
        HomeDirectory = source.HomeDirectory;
        SourceFile = source.SourceFile;
        Output = source.Output;
        Error = source.Error;
        DryRun = source.DryRun;
        BufferSize = source.BufferSize;
        SpoolLimit = source.SpoolLimit;
        Timeline = source.Timeline;
        Observe = source.Observe;
        ReportError = source.ReportError;
        directoryChanged = source.directoryChanged;
        captured = this;
    }

    public string WorkingDirectory { get; private set; }
    public string HomeDirectory { get; }
    public string? SourceFile { get; }
    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public bool DryRun { get; }
    public int BufferSize { get; }
    public long? SpoolLimit { get; }
    public RuntimeTimeline? Timeline { get; }
    public Action<RuntimeObservation>? Observe { get; }
    public Action<MsyncException>? ReportError { get; }
    public NativeRuntimeContext CaptureDirectory() => captured ??= new(this);
    public string LocalPath(NativePathValue value) => value.LocalPath(WorkingDirectory, HomeDirectory);
    public NativePathValue Capture(NativePathValue value) => value with
    {
        WorkingDirectory = value.WorkingDirectory ?? WorkingDirectory,
        HomeDirectory = value.HomeDirectory ?? HomeDirectory
    };

    internal NativePathValue ChangeWorkingDirectory(NativePathValue path)
    {
        if (path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            throw new MsyncException("cd changes the local execution directory; qualify remote command paths with 'connection @ path'.");
        string directory = Path.GetFullPath(LocalPath(path));
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            throw new IOException($"Cannot cd to '{directory}': it is not a directory.");
        NativeAccess.SearchDirectory(directory);
        NativeRuntimeContext authority = directoryAuthority ?? this;
        authority.WorkingDirectory = directory;
        authority.captured = null;
        authority.directoryChanged?.Invoke(directory);
        return new(directory, directory, HomeDirectory: HomeDirectory);
    }
}
