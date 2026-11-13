using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public sealed class NativeRuntimeContext
{
    private readonly TextReader? input;
    private readonly Action<string>? directoryChanged;
    private NativeRuntimeContext? captured;
    private bool ownsDirectory;
    private readonly NativeRuntimeContext? directoryAuthority;

    public NativeRuntimeContext(ExecutionOptions? options = null, BindingContext? binding = null)
    {
        options ??= new();
        ownsDirectory = binding is not null || options.WorkingDirectory is not null;
        WorkingDirectory = binding?.WorkingDirectory ?? Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        HomeDirectory = binding?.HomeDirectory ?? HostContext.HomeDirectory;
        SourceFile = binding?.SourceFile ?? options.SourceFile;
        input = options.Input;
        Output = options.Output ?? Console.Out;
        Error = options.Error ?? Console.Error;
        DryRun = options.DryRun;
        Jobs = options.Jobs;
        BufferSize = options.BufferSize;
        MemoryLimit = options.MemoryLimit;
        SpoolLimit = options.SpoolLimit;
        Timeline = options.Timeline;
        Observe = options.Observe;
        ReportError = options.ReportError;
        directoryChanged = options.WorkingDirectoryChanged;
    }

    private NativeRuntimeContext(NativeRuntimeContext source, bool snapshot)
    {
        directoryAuthority = snapshot ? source : null;
        ownsDirectory = true;
        WorkingDirectory = source.WorkingDirectory;
        HomeDirectory = source.HomeDirectory;
        SourceFile = source.SourceFile;
        input = source.input;
        Output = source.Output;
        Error = source.Error;
        DryRun = source.DryRun;
        Jobs = source.Jobs;
        BufferSize = source.BufferSize;
        MemoryLimit = source.MemoryLimit;
        SpoolLimit = source.SpoolLimit;
        Timeline = source.Timeline;
        Observe = source.Observe;
        ReportError = source.ReportError;
        directoryChanged = source.directoryChanged;
        TransferAsync = source.TransferAsync;
        ProcessCallAsync = source.ProcessCallAsync;
        ProcessCreated = source.ProcessCreated;
        DescribeFunction = source.DescribeFunction;
        DescribeValue = source.DescribeValue;
        DescribeField = source.DescribeField;
        captured = snapshot ? this : null;
    }

    public string WorkingDirectory { get; private set; }
    public string HomeDirectory { get; }
    public string? SourceFile { get; }
    public TextReader Input => input ?? Console.In;
    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public bool DryRun { get; }
    public int Jobs { get; }
    public int BufferSize { get; }
    public long MemoryLimit { get; }
    public long? SpoolLimit { get; }
    public RuntimeTimeline? Timeline { get; }
    public Action<RuntimeObservation>? Observe { get; }
    public Action<MsyncException>? ReportError { get; }
    public Func<NativeValue, NativePathValue, NativeRuntimeContext, CancellationToken, ValueTask<bool>>? TransferAsync { get; set; }
    public Func<NativePathValue, IReadOnlyList<NativeValue>, NativeRuntimeContext, CancellationToken, ValueTask<NativeValue>>? ProcessCallAsync { get; set; }
    public Action<NativeProcess>? ProcessCreated { get; set; }
    public Func<NativeFunction, NativeCallableDescription?>? DescribeFunction { get; set; }
    public Func<NativeValue, TypeRef>? DescribeValue { get; set; }
    public Func<NativeAddress, int, NativeFieldDescription>? DescribeField { get; set; }

    public NativeRuntimeContext CaptureDirectory() => captured ??= new(this, snapshot: true);
    internal NativeRuntimeContext ForkDirectory() => new(this, snapshot: false);
    public NativePathValue AsPath(NativeValue value) => value.Kind switch
    {
        NativeKind.Path => value.AsPath(),
        NativeKind.String => new(value.AsString(), WorkingDirectory, HomeDirectory: HomeDirectory),
        _ => throw new MsyncException($"Expected Path or String, received {value.Type}")
    };
    public string LocalPath(NativeValue value) => AsPath(value).LocalPath(WorkingDirectory, HomeDirectory);
    public string LocalPath(NativePathValue value) => value.LocalPath(WorkingDirectory, HomeDirectory);
    public NativePathValue Capture(NativePathValue value) => value with
    {
        WorkingDirectory = value.WorkingDirectory ?? WorkingDirectory,
        HomeDirectory = value.HomeDirectory ?? HomeDirectory
    };

    internal void AdoptAmbientDirectory()
    {
        if (ownsDirectory) return;
        string directory = Path.GetFullPath(Environment.CurrentDirectory);
        if (directory == WorkingDirectory) return;
        WorkingDirectory = directory;
        captured = null;
    }

    public NativePathValue ChangeWorkingDirectory(NativeValue value)
    {
        NativePathValue path = AsPath(value);
        if (path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            throw new MsyncException("cd changes the local execution directory; qualify remote command paths with 'connection @ path'.");
        string directory = Path.GetFullPath(LocalPath(path));
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            throw new IOException($"Cannot cd to '{directory}': it is not a directory.");
        NativeAccess.SearchDirectory(directory);
        NativeRuntimeContext authority = directoryAuthority ?? this;
        authority.ownsDirectory = true;
        authority.WorkingDirectory = directory;
        authority.captured = null;
        authority.directoryChanged?.Invoke(directory);
        return new(directory, directory, HomeDirectory: HomeDirectory);
    }
}

public sealed record NativeCallableDescription(IReadOnlyList<TypeRef> Parameters, TypeRef Result, bool Variadic = false);
public sealed record NativeFieldDescription(TypeRef StorageType, bool AdmitsNull);
