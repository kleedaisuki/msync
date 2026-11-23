using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed unsafe partial class HpirSessionState
{
    private Dictionary<HpirPathOrigin, BindingContext>? pathContexts;
    private Dictionary<HpirPathOrigin, nint>? pathRecords;
    private Dictionary<string, nint>? pathAnchorTexts;

    // Reuse the session Text shape even when a live pwd Path becomes a String.
    private nint DirectoryAnchor(string directory) => PathAnchor(directory, active!.Program.TextLayout, heap!);

    private void CapturePreparedPaths(BindingContext? context, RuntimeTimelineScope? trace)
    {
        if (active!.PreparedPathOrigins is not { Count: > 0 } origins) return;
        var captured = pathContexts ??= [];
        int added = 0;
        foreach (var origin in origins)
            if (pathRecords?.ContainsKey(origin) != true && captured.TryAdd(origin, context!)) ++added;
        trace?.SetMetric("paths.prepared_captures", added);
    }

    private nint LinkPath(HpirSessionPathLiteral literal, HpirProgram program, int layout, HpirHeap owner, ref int allocated)
    {
        var records = pathRecords ??= [];
        if (records.TryGetValue(literal.Origin, out nint address)) return address;
        BindingContext context = pathContexts?.GetValueOrDefault(literal.Origin) ?? program.Context!;
        address = owner.Allocate(layout, 1, clear: false).Address;
        var anchors = new HpirPathAnchors(PathAnchor(context.WorkingDirectory, program.TextLayout, owner),
            PathAnchor(context.HomeDirectory, program.TextLayout, owner));
        *(HpirPathValue*)address = anchors.Capture(Text(literal.Raw, program.TextLayout, owner));
        records.Add(literal.Origin, address);
        pathContexts?.Remove(literal.Origin);
        ++allocated;
        return address;
    }

    private nint PathAnchor(string text, int layout, HpirHeap owner)
    {
        var anchors = pathAnchorTexts ??= new(StringComparer.Ordinal);
        if (anchors.TryGetValue(text, out nint address)) return address;
        address = Text(text, layout, owner);
        anchors.Add(text, address);
        return address;
    }

    private int MarkPaths(HpirHeap owner)
    {
        int marked = 0;
        if (pathRecords is not null)
            foreach (nint address in pathRecords.Values)
                if (owner.Mark(address)) ++marked;
        if (pathAnchorTexts is not null)
            foreach (nint address in pathAnchorTexts.Values)
                if (owner.Mark(address)) ++marked;
        return marked;
    }

    private void ClearPaths()
    {
        pathRecords = null;
        pathContexts = null;
        pathAnchorTexts = null;
    }
}
