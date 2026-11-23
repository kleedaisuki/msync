using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using HostMemory = System.Runtime.InteropServices.NativeMemory;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed unsafe class HpirDirectoryAuthority : IDisposable
{
    private readonly ExecutionOptions? options;
    private readonly AnchorBank? anchors;
    private readonly Func<string, nint> anchor;
    private string? ownedDirectory;
    private BindingContext? binding;
    private nint directory;

    internal HpirDirectoryAuthority(ExecutionOptions? options, BindingContext? initial = null, Func<string, nint>? sessionAnchor = null)
    {
        this.options = options;
        if (sessionAnchor is null)
        {
            anchors = new();
            anchor = anchors.Text;
        }
        else anchor = sessionAnchor;
        ownedDirectory = initial?.WorkingDirectory
            ?? (options?.WorkingDirectory is { } configured ? Path.GetFullPath(configured) : null);
    }

    private HpirDirectoryAuthority(Func<string, nint> anchor, BindingContext binding, nint directory, ExecutionOptions? options)
    {
        this.anchor = anchor;
        this.binding = binding;
        this.directory = directory;
        this.options = options;
        ownedDirectory = binding.WorkingDirectory;
    }

    internal string WorkingDirectory => ownedDirectory ?? Path.GetFullPath(Environment.CurrentDirectory);
    internal BindingContext CurrentBinding() => new(WorkingDirectory, HostContext.HomeDirectory, options?.SourceFile);
    internal BindingContext? Snapshot => binding;
    internal nint Directory => directory;

    internal void BeginExecution(BindingContext? image) => binding = image ?? CurrentBinding();

    internal HpirDirectoryAuthority Fork(nint pointer) =>
        new(anchor, binding!, directory == 0 ? pointer : directory, options);

    internal nint Change(NativePathValue path, HpirWorkSite site, bool publish)
    {
        using var trace = site.Begin("hpir.directory.change");
        BindingContext current = binding ?? CurrentBinding();
        trace?.SetAttribute("directory.previous", current.WorkingDirectory);
        trace?.SetAttribute("directory.input", path.Raw);
        try
        {
            // Native owns the shared local endpoint/search policy, not this session.
            var host = new NativeRuntimeContext(binding: current);
            string next = host.ChangeWorkingDirectory(path).WorkingDirectory!;
            ownedDirectory = next;
            if (publish)
            {
                binding = current with { WorkingDirectory = next };
                directory = anchor(next);
            }
            trace?.SetAttribute("directory.current", next);
            options?.WorkingDirectoryChanged?.Invoke(next);
            return directory;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            throw;
        }
    }

    internal void EndExecution()
    {
        binding = null;
        directory = 0;
    }

    public void Dispose()
    {
        anchors?.Dispose();
    }

    // Earlier Path values, including those escaping cooperative children, still borrow these anchors.
    private sealed class AnchorBank : IDisposable
    {
        private Dictionary<string, nint>? addresses;
        internal nint Text(string value)
        {
            var table = addresses ??= new(StringComparer.Ordinal);
            if (table.TryGetValue(value, out nint address)) return address;
            address = (nint)HostMemory.Alloc(checked((nuint)value.Length * HpirScalarImports.TextElementBytes + HpirScalarImports.TextHeaderBytes));
            HpirTextStorage.Write(address, value);
            table.Add(value, address);
            return address;
        }
        public void Dispose()
        {
            if (addresses is null) return;
            foreach (nint address in addresses.Values) HostMemory.Free((void*)address);
            addresses = null;
        }
    }
}
