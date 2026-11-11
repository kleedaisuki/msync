using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

// A cosmetic revision moves complete, byte-identical statements. Whitespace
// inside a statement can be part of a bare Path, so this is not token-only reuse.
internal sealed class EditorSourceMap(EditorSourceMap.Gap[] gaps, int oldLength, string source)
{
    internal readonly record struct Gap(int Start, int End, int NewStart, int NewEnd);

    internal static EditorSourceMap? Match(string previous, EditorParseResult old,
        string source, EditorParseResult current, CancellationToken cancellationToken)
    {
        if (old.Error is not null || current.Error is not null
            || old.Document.Statements.Count != current.Document.Statements.Count
            || old.Tokens.Count != current.Tokens.Count) return null;
        List<Gap> gaps = [];
        int end = 0, newEnd = 0;
        for (int index = 0; index < old.Document.Statements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = old.Document.Statements[index];
            var right = current.Document.Statements[index];
            if (left.GetType() != right.GetType() || !previous.AsSpan(left.Span.Start, left.Span.Length)
                .SequenceEqual(source.AsSpan(right.Span.Start, right.Span.Length))) return null;
            if (!Trivia(end, left.Span.Start, newEnd, right.Span.Start)) return null;
            end = left.Span.End;
            newEnd = right.Span.End;
        }
        if (!Trivia(end, previous.Length, newEnd, source.Length)) return null;
        for (int index = 0; index < old.Tokens.Count; index++)
            if (old.Tokens[index].Kind != current.Tokens[index].Kind
                || old.Tokens[index].Text != current.Tokens[index].Text) return null;
        return new(gaps.ToArray(), previous.Length, source);

        bool Trivia(int start, int stop, int nextStart, int nextStop)
        {
            var before = previous.AsSpan(start, stop - start);
            var after = source.AsSpan(nextStart, nextStop - nextStart);
            if (before.SequenceEqual(after)) return true;
            // Keep comment/documentation content and its placement exact.
            if (!before.IsWhiteSpace() || !after.IsWhiteSpace()) return false;
            gaps.Add(new(start, stop, nextStart, nextStop));
            return true;
        }
    }

    private int Offset(int offset, bool end = false)
    {
        int first = 0, last = gaps.Length;
        while (first < last)
        {
            int middle = (first + last) / 2;
            if (gaps[middle].Start <= offset) first = middle + 1;
            else last = middle;
        }
        if (first == 0) return offset;
        Gap gap = gaps[first - 1];
        if (offset > gap.End || offset == gap.End && (!end || gap.Start < gap.End))
            return gap.NewEnd + offset - gap.End;
        return gap.NewStart + Math.Min(offset - gap.Start, gap.NewEnd - gap.NewStart);
    }

    private SourceSpan Span(SourceSpan span)
    {
        int start = Offset(span.Start);
        return span.Length == 0 ? new(start, 0) : new(start, Offset(span.End, end: true) - start);
    }

    private SourceSpan Owner(SourceSpan span) => IsRoot(span) ? new(0, source.Length) : Span(span);
    private bool IsRoot(SourceSpan span) => span.Start == 0 && span.Length == oldLength;

    internal SemanticObservationView Rebase(SemanticObservationView view)
    {
        var module = view.Module;
        Dictionary<ImmutableDictionary<string, SemanticConceptView>,
            ImmutableDictionary<string, SemanticConceptView>> conceptMaps = new(ReferenceEqualityComparer.Instance);
        return view with
        {
            Module = module with
            {
                Sources = module.Sources.SetItem(view.Source.Value,
                    module.Sources[view.Source.Value] with { Text = source }),
                Locations = [.. module.Locations.Select(location => location with { Source = Range(location.Source) })]
            },
            Values = [.. view.Values.Select(value => value.Members.IsDefaultOrEmpty ? value : value with
            {
                Members = [.. value.Members.Select(field => field.Definitions.IsDefaultOrEmpty ? field
                    : field with { Definitions = [.. field.Definitions.Select(Range)] })]
            })],
            Observations = [.. view.Observations.Select(item => item with { Span = Range(item.Span) })],
            Contexts = [.. view.Contexts.Select(item => item with
            {
                Owner = Owner(item.Owner),
                Point = IsRoot(item.Owner) && item.Point == 0 ? 0 : Offset(item.Point, item.After),
                Concepts = Concepts(item.Concepts)
            })],
            Receivers = [.. view.Receivers.Select(item => item with { Span = Span(item.Span) })],
            PathOwners = [.. view.PathOwners.Select(item => item with { Span = Span(item.Span) })],
            Imports = [.. view.Imports.Select(item => item with { Span = Span(item.Span) })],
            References = [.. view.References.Select(item => item with
            {
                Span = Span(item.Span),
                Definitions = [.. item.Definitions.Select(Range)]
            })]
        };

        HirSourceRange Range(HirSourceRange range)
        {
            if (range.Source != view.Source) return range;
            SourceSpan span = Span(new(range.Start, range.Length));
            return range with { Start = span.Start, Length = span.Length };
        }

        ImmutableDictionary<string, SemanticConceptView> Concepts(ImmutableDictionary<string, SemanticConceptView> map)
        {
            if (map.Count == 0) return map;
            if (conceptMaps.TryGetValue(map, out var known)) return known;
            var rebased = map.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value with { Definition = Range(pair.Value.Definition) }, map.KeyComparer);
            conceptMaps.Add(map, rebased);
            return rebased;
        }
    }
}
