using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private Dictionary<long, DirectoryCursor>? directories;
    private long nextDirectory;

    internal long OpenDirectory(NativePathValue path, bool recursive, NativeRuntimeContext context, HpirWorkSite site,
        string? pattern = null, IReadOnlyList<HpirLocalPath>? members = null)
    {
        long id = checked(++nextDirectory);
        var cursor = new DirectoryCursor(path, recursive, context, site, pattern, members);
        (directories ??= []).Add(id, cursor);
        using RuntimeTimelineScope? trace = cursor.Begin(site, "hpir.directory.open", id);
        return id;
    }

    internal NativePathValue? PullDirectory(long id, HpirWorkSite site, CancellationToken token)
    {
        DirectoryCursor cursor = directories![id];
        using RuntimeTimelineScope? trace = cursor.Begin(site, "hpir.directory.next", id);
        try
        {
            token.ThrowIfCancellationRequested();
            if (cursor.Prepared)
            {
                NativePathValue? selected = cursor.PullPrepared();
                trace?.SetMetric("directory.yielded", selected.HasValue ? 1 : 0);
                return selected;
            }
            if (cursor.Entries is null)
            {
                WaitDirectory(AwaitLiveRead(cursor.Path, site, Ordering, token), token);
                cursor.Start(token);
                trace?.SetMetric("directory.first_pull", 1);
            }
            bool available = cursor.Next(this, token);
            trace?.SetMetric("directory.yielded", available ? 1 : 0);
            return available ? cursor.Entries!.Current : null;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            if (error is OperationCanceledException or MsyncException { Span: not null }) throw;
            throw new MsyncException($"Cannot enumerate '{cursor.Path}': {error.Message}", site.Source, error);
        }
    }

    internal void CloseDirectory(long id, HpirWorkSite site)
    {
        if (!directories!.Remove(id, out var cursor)) return;
        using RuntimeTimelineScope? trace = cursor.Begin(site, "hpir.directory.close", id);
        trace?.SetAttribute("cleanup.reason", cursor.Exhausted ? "exhausted" : "explicit");
        CloseDirectory(cursor, site, trace);
    }

    private void CloseDirectories(ref List<Exception>? failures, bool abort, RuntimeTimelineScope? trace)
    {
        if (directories is not { Count: > 0 } cursors) return;
        trace?.SetMetric("directory_cursors.closed", cursors.Count);
        foreach (var (id, cursor) in cursors.Reverse())
        {
            using var close = cursor.Begin(cursor.Site, "hpir.directory.close", id);
            close?.SetAttribute("cleanup.reason", abort ? "unwind" : "settle");
            try { CloseDirectory(cursor, cursor.Site, close); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        cursors.Clear();
    }

    private static void CloseDirectory(DirectoryCursor cursor, HpirWorkSite site, RuntimeTimelineScope? trace)
    {
        if (cursor.Entries is null) return;
        try
        {
            ValueTask close = cursor.Entries.DisposeAsync();
            if (!close.IsCompletedSuccessfully) close.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            throw new MsyncException($"Cannot close directory '{cursor.Path}': {error.Message}", site.Source, error);
        }
    }

    private void WaitDirectory(ValueTask readiness, CancellationToken token)
    {
        if (readiness.IsCompletedSuccessfully) return;
        Task task = readiness.AsTask();
        if (!task.IsCompleted) Guests?.PumpHostDemand(task, token);
        task.GetAwaiter().GetResult();
    }

    private sealed class DirectoryCursor(NativePathValue path, bool recursive, NativeRuntimeContext context, HpirWorkSite site,
        string? pattern, IReadOnlyList<HpirLocalPath>? members)
    {
        private int memberIndex;
        internal bool Prepared => members is not null;

        internal NativePathValue? PullPrepared()
        {
            if (memberIndex == members!.Count)
            {
                Exhausted = true;
                return null;
            }
            var entry = members[memberIndex++];
            return path.WithRaw(entry.Raw, OperatingSystem.IsWindows() ? entry.WindowsSpelling : null);
        }

        internal HpirWorkSite Site => site;
        internal NativePathValue Path => path;
        internal IAsyncEnumerator<NativePathValue>? Entries { get; private set; }
        internal bool Exhausted { get; private set; }

        internal void Start(CancellationToken token) =>
            Entries = (pattern is null ? NativeBuiltins.OpenDirectory(path, recursive, context, token)
                : NativeHostIO.OpenSelectedPaths(path, pattern, context, token)).GetAsyncEnumerator();

        internal bool Next(HpirWork owner, CancellationToken token)
        {
            if (Exhausted) return false;
            ValueTask<bool> pending = Entries!.MoveNextAsync();
            bool available = pending.IsCompletedSuccessfully ? pending.Result : Wait(owner, pending.AsTask(), token);
            Exhausted = !available;
            return available;
        }

        private static bool Wait(HpirWork owner, Task<bool> task, CancellationToken token)
        {
            if (!task.IsCompleted) owner.Guests?.PumpHostDemand(task, token);
            return task.GetAwaiter().GetResult();
        }

        internal RuntimeTimelineScope? Begin(HpirWorkSite location, string name, long id)
        {
            var trace = location.Begin(name);
            if (trace is null) return null;
            trace.SetAttribute("directory.path", path.ToString());
            trace.SetMetric("cursor.id", id);
            trace.SetMetric("directory.recursive", recursive ? 1 : 0);
            if (pattern is not null)
            {
                trace.SetAttribute("selection.pattern", pattern);
                trace.SetAttribute("selection.membership", members is null ? "live" : "frozen");
                if (members is not null) trace.SetMetric("selection.members", members.Count);
            }
            return trace;
        }
    }
}
