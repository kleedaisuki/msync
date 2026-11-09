using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class EditorContextCollector
{
    private sealed class ReferenceOrigins(bool concrete)
    {
        public bool Concrete { get; set; } = concrete;
        public List<EditorLocation> Locations { get; } = [];
    }
    private readonly Dictionary<SourceSpan, ReferenceOrigins> references = [];

    public void Reference(Frame frame, SourceSpan span, IReadOnlyList<EditorLocation> locations)
    {
        if (!references.TryGetValue(span, out var origins)) references[span] = origins = new(frame.EditorReportErrors);
        if (origins.Concrete && !frame.EditorReportErrors) return;
        if (!origins.Concrete && frame.EditorReportErrors) origins.Locations.Clear();
        origins.Concrete |= frame.EditorReportErrors;
        foreach (EditorLocation location in locations)
            if (!origins.Locations.Contains(location)) origins.Locations.Add(location);
    }

    public IReadOnlyList<EditorLocation> DefinitionsAt(int offset)
    {
        ReferenceOrigins? match = null;
        int length = int.MaxValue;
        foreach (var (span, origins) in references)
            if (span.Start <= offset && offset < span.End && span.Length < length)
            { match = origins; length = span.Length; }
        if (match is not null) return match.Locations;
        foreach (var (span, origins) in references)
            if (span.End == offset && span.Length < length)
            { match = origins; length = span.Length; }
        return match?.Locations ?? [];
    }
}
