using System.Runtime.CompilerServices;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private sealed class EditorSourceSpanComparer : IEqualityComparer<(string Source, SourceSpan Span)>
    {
        public bool Equals((string Source, SourceSpan Span) left, (string Source, SourceSpan Span) right) =>
            ReferenceEquals(left.Source, right.Source) && left.Span == right.Span;
        public int GetHashCode((string Source, SourceSpan Span) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Source), key.Span);
    }
    private readonly HashSet<(string Source, SourceSpan Span)>? editorParameterDefinitions = observe is null ? null : new(new EditorSourceSpanComparer());
    private readonly HashSet<SourceSpan>? editorParameterOccurrences = observe is null ? null : [];

    private void CaptureParameter(Frame frame, SourceSpan span)
    {
        editorParameterDefinitions?.Add((frame.Source, span));
        if (IsCurrentSource(frame)) editorParameterOccurrences?.Add(span);
    }

    internal bool IsParameterOccurrence(SourceSpan span) => editorParameterOccurrences?.Contains(span) == true;

    private void CaptureConceptDefinition(Frame frame, TypeSyntax syntax)
    {
        if (editorContexts is null || !TryConcept(frame, syntax.Name, out var definition)) return;
        if (definition.Syntax.NameSpan is { } name)
        {
            SourceSpan selected = EditorTypeNames.Parts(frame.Source, syntax).Last().Span;
            CaptureDefinition(frame, selected, [new(definition.SourceName, definition.Source, name)]);
        }
    }

    private EditorLocation? DeclarationOrigin(Frame frame, SourceSpan? span) =>
        editorContexts is not null && span is { Length: > 0 } name ? new(frame.SourceName, frame.Source, name) : null;

    private void CaptureDefinition(Frame frame, SourceSpan span, IReadOnlyList<EditorLocation> origins)
    {
        if (editorContexts is null || !IsCurrentSource(frame)) return;
        editorContexts.Reference(frame, span, origins);
        if (origins.Any(origin => editorParameterDefinitions!.Contains((origin.Source, origin.Span))))
            editorParameterOccurrences!.Add(span);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<EditorLocation>>? JoinOrigins(ScopeInfo left, ScopeInfo right, IEnumerable<string> members)
    {
        if (left.Origins is null && right.Origins is null) return null;
        Dictionary<string, IReadOnlyList<EditorLocation>> result = new(StringComparer.Ordinal);
        foreach (string name in members)
        {
            IReadOnlyList<EditorLocation>? a = null, b = null;
            left.Origins?.TryGetValue(name, out a);
            right.Origins?.TryGetValue(name, out b);
            if (a is not null || b is not null) result[name] = (a ?? []).Concat(b ?? []).Distinct().ToArray();
        }
        return result;
    }
}
