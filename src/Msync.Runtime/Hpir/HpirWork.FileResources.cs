using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private FileTransfers? fileTransfers;
    private FileResources? retainedFileResources;

    private FileTransfers Files(ExecutionOptions? options, BindingContext? binding) =>
        fileTransfers ??= new(options, binding, retainedFileResources);

    private sealed class FileTransfers(ExecutionOptions? options, BindingContext? binding, FileResources? inherited) : IDisposable
    {
        internal readonly NativeRuntimeContext Context = new NativeRuntimeContext(options, binding).CaptureDirectory();
        internal readonly SemaphoreSlim Capacity = new(TransferCapacity(options));
        internal Task?[]? PreparedTasks;
        internal FileResources? Resources { get; private set; } = inherited;

        internal FileResources NeedResources() => Resources ??= new();
        public void Dispose() => Capacity.Dispose();
    }

    private readonly record struct FileKeys(ResourceKey Primary, ResourceKey? Alias = null)
    {
        internal void Collect(ResourceIndex index, bool writing, HashSet<Task> tasks)
        {
            index.Collect(Primary, writing, tasks);
            if (Alias is { } alias) index.Collect(alias, writing, tasks);
        }
    }

    private readonly record struct FileWaits(HashSet<Task> Tasks, int Cross);

    private readonly record struct FileLease(ResourceIndex Index, TaskCompletionSource Signal,
        ResourceIndex.Registration Primary, ResourceIndex.Registration ReadAlias, ResourceIndex.Registration WriteAlias)
    {
        internal Task Completion => Signal.Task;
    }

    private readonly record struct FileAdmission(FileResources Owner, FileLease Lease, FileWaits Waits)
    {
        internal void Release() => Owner.Release(Lease);
    }

    // One ownership frontier and lock, with a partition for prelinked sites:
    // only dependencies crossing its boundary need discovery during execution.
    private sealed partial class FileResources
    {
        private readonly Lock gate = new();
        private readonly ResourceIndex residual = new();
        private ResourceIndex? prepared;
        private int activeLeases;

        internal bool HasRegistrations => Volatile.Read(ref activeLeases) != 0;

        internal FileWaits BeforeRead(FileKeys keys)
        {
            HashSet<Task> result = [];
            lock (gate)
            {
                keys.Collect(residual, false, result);
                int residualCount = result.Count;
                if (prepared is { } other) keys.Collect(other, false, result);
                return new(result, result.Count - residualCount);
            }
        }

        internal FileAdmission Admit(FileKeys? read, FileKeys write, bool sharedTransfer)
        {
            HashSet<Task> result = [];
            lock (gate)
            {
                Collect(residual, read, write, result);
                int residualCount = result.Count;
                if (!sharedTransfer && prepared is { } other) Collect(other, read, write, result);
                var index = sharedTransfer ? (prepared ??= new()) : residual;
                var lease = Register(index, read, write);
                return new(this, lease, new(result, sharedTransfer ? residualCount : result.Count - residualCount));
            }
        }

        internal FileLease AdmitRead(FileKeys read)
        {
            lock (gate) return Register(residual, read, null);
        }

        internal void Release(FileLease lease)
        {
            lock (gate)
            {
                lease.Index.Remove(lease.Completion, lease.Primary);
                if (lease.ReadAlias.Reader is not null) lease.Index.Remove(lease.Completion, lease.ReadAlias);
                if (lease.WriteAlias.Writer is not null) lease.Index.Remove(lease.Completion, lease.WriteAlias);
                --activeLeases;
            }
            lease.Signal.TrySetResult();
        }

        private static void Collect(ResourceIndex index, FileKeys? read, FileKeys write, HashSet<Task> tasks)
        {
            read?.Collect(index, false, tasks);
            write.Collect(index, true, tasks);
        }

        private FileLease Register(ResourceIndex index, FileKeys? read, FileKeys? write)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ResourceIndex.Registration primary = write is { } destination
                ? index.Add(completion.Task, read?.Primary, destination.Primary)
                : index.AddRead(completion.Task, read!.Value.Primary);
            var readAlias = read?.Alias is { } alternate ? index.AddRead(completion.Task, alternate) : default;
            var writeAlias = write?.Alias is { } output ? index.Add(completion.Task, null, output) : default;
            ++activeLeases;
            return new(index, completion, primary, readAlias, writeAlias);
        }
    }

    private static FileKeys FileKeysFor(NativePathValue path, NativeRuntimeContext origin) =>
        path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote
            ? new(new(remote.ToString(), ""))
            : FileKeysFor(path.LocalPath(origin.WorkingDirectory, origin.HomeDirectory));

    private static FileKeys FileKeysFor(string lexical)
    {
        string physical = ResourcePath.Resolve(lexical);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return new(new("local", lexical), string.Equals(lexical, physical, comparison) ? null : new ResourceKey("local", physical));
    }

    private static int TransferCapacity(ExecutionOptions? options) => options is null ? 4 :
        Math.Max(1, Math.Min(options.Jobs, (int)Math.Min(int.MaxValue, options.MemoryLimit / Math.Max(1, options.BufferSize))));

    private void ResetFileTransfers(bool keepReads)
    {
        retainedFileResources = keepReads ? fileTransfers?.Resources ?? retainedFileResources : null;
        fileTransfers?.Dispose();
        fileTransfers = null;
    }
}
