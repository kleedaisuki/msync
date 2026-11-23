using Msync.Core;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private Dictionary<long, TextCursor>? textCursors;
    private long nextTextCursor;

    internal long OpenTextRange(TextReader reader, HpirWorkSite site) => OpenTextRange(new TextCursor(reader, site), site);

    internal long OpenTextRange(NativePathValue path, NativeRuntimeContext context, HpirWorkSite site) =>
        OpenTextRange(new TextCursor(path, context, site), site);

    private long OpenTextRange(TextCursor cursor, HpirWorkSite site)
    {
        long id = checked(++nextTextCursor);
        (textCursors ??= []).Add(id, cursor);
        using RuntimeTimelineScope? trace = site.Begin("hpir.text_range.open");
        cursor.Describe(trace, id);
        return id;
    }

    internal string? PullTextRange(long id, CancellationToken token, HpirWorkSite site)
    {
        TextCursor cursor = textCursors![id];
        using RuntimeTimelineScope? trace = site.Begin("hpir.text_range.next");
        cursor.Describe(trace, id);
        try
        {
            if (cursor.Exhausted) return null;
            token.ThrowIfCancellationRequested();
            ValueTask<string?> read = cursor.Read(this, site, token);
            trace?.SetMetric("await.suspended", read.IsCompleted ? 0 : 1);
            string? line = read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
            cursor.Exhausted = line is null;
            trace?.SetMetric("range.yielded", line is null ? 0 : 1);
            trace?.SetMetric("range.exhausted", line is null ? 1 : 0);
            trace?.SetMetric("input.characters", line?.Length ?? 0);
            return line;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            if (error is OperationCanceledException or MsyncException { Span: not null }) throw;
            throw new MsyncException($"Cannot read {cursor.Resource}: {error.Message}", site.Source, error);
        }
    }

    internal void CloseTextRange(long id, HpirWorkSite site)
    {
        TextCursor cursor = textCursors![id];
        textCursors.Remove(id);
        CloseTextRange(cursor, id, site, cursor.Exhausted ? "exhausted" : "explicit");
    }

    private void CloseTextRanges(ref List<Exception>? failures, bool abort, RuntimeTimelineScope? trace)
    {
        if (textCursors is not { Count: > 0 } cursors) return;
        trace?.SetMetric("text_cursors.closed", cursors.Count);
        foreach (var (id, cursor) in cursors.OrderByDescending(static pair => pair.Key))
        {
            try { CloseTextRange(cursor, id, cursor.Site, abort ? "unwind" : "settle"); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        cursors.Clear();
    }

    private static void CloseTextRange(TextCursor cursor, long id, HpirWorkSite site, string reason)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.text_range.close");
        cursor.Describe(trace, id);
        trace?.SetAttribute("cleanup.reason", reason);
        try
        {
            ValueTask close = cursor.DisposeAsync();
            if (close.IsCompletedSuccessfully) close.GetAwaiter().GetResult();
            else close.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            throw new MsyncException($"Cannot close {cursor.Resource}: {error.Message}", site.Source, error);
        }
    }

    private sealed class TextCursor
    {
        private readonly TextReader? reader;
        private readonly SemaphoreSlim? turn;
        private readonly NativeBuiltins.TextLineInput? file;
        private readonly NativePathValue path;
        private bool startedFile;

        internal TextCursor(TextReader reader, HpirWorkSite site)
        {
            this.reader = reader;
            turn = StandardTextInput.Turn(reader);
            Site = site;
        }

        internal TextCursor(NativePathValue path, NativeRuntimeContext context, HpirWorkSite site)
        {
            this.path = path;
            file = new(path, context);
            Site = site;
        }

        internal HpirWorkSite Site { get; }
        internal bool Exhausted { get; set; }
        internal string Resource => file is null ? "standard input" : $"'{path}'";

        internal ValueTask<string?> Read(HpirWork owner, HpirWorkSite site, CancellationToken token)
        {
            if (file is null) return StandardTextInput.ReadLine(reader!, turn!, Site.Timeline, token);
            if (startedFile) return file.Read(token);
            startedFile = true;
            Task? ordering = owner.Ordering;
            return owner.fileTransfers?.Resources is null && ordering is null
                ? file.Read(token) : FirstFileRead(owner, site, ordering, token);
        }

        private async ValueTask<string?> FirstFileRead(HpirWork owner, HpirWorkSite site, Task? ordering, CancellationToken token)
        {
            await owner.AwaitLiveRead(path, site, ordering, token).ConfigureAwait(false);
            return await file!.Read(token).ConfigureAwait(false);
        }

        internal ValueTask DisposeAsync() => file?.DisposeAsync() ?? ValueTask.CompletedTask;

        internal void Describe(RuntimeTimelineScope? trace, long id)
        {
            if (trace is null) return;
            trace.SetAttribute("input.kind", file is null ? "stdin" : "file");
            trace.SetAttribute("input.resource", Resource);
            trace.SetMetric("cursor.id", id);
        }
    }
}
