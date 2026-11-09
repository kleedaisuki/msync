using Msync.Compiler;
using Msync.Compiler.Syntax;

namespace Msync.Cli;

internal sealed record TypeCompletionContext(SourceSpan Span, string Qualifier, string Prefix);

internal static class TypeCompletion
{
    public static TypeCompletionContext Find(string source, int caret)
    {
        SourceSpan span = NameSyntax.SpanAt(source, caret);
        int start = span.Start;
        if (start > 0 && source[start - 1] == '$') start--;
        span = new(start, span.End - start);
        List<string> segments = [];
        int cursor = start;
        while (cursor > 0)
        {
            while (cursor > 0 && char.IsWhiteSpace(source[cursor - 1])) cursor--;
            if (cursor < 2 || source[cursor - 1] != ':' || source[cursor - 2] != ':') break;
            cursor -= 2;
            while (cursor > 0 && char.IsWhiteSpace(source[cursor - 1])) cursor--;
            int end = cursor;
            while (cursor > 0 && !NameSyntax.Boundary(source[cursor - 1])) cursor--;
            if (cursor > 0 && source[cursor - 1] == '$') cursor--;
            if (end == cursor) break;
            segments.Add(source[cursor..end]);
        }
        segments.Reverse();
        string qualifier = segments.Count == 0 ? "" : string.Join("::", segments) + "::";
        return new(span, qualifier, source[start..caret]);
    }

    public static IReadOnlyList<SemanticMember> Candidates(TypeCompletionContext context, EditorContext? insight)
    {
        IEnumerable<SemanticMember> candidates = insight?.Bindings.Where(member => member.IsConstraint) ?? [];
        if (context.Qualifier.Length == 0)
        {
            candidates = LanguageTypes.Names.Select(name => new SemanticMember(name, "Type", name,
                name == "Range" ? "Range<T> constrains the element type." : "A built-in type constraint.", SemanticRole: "type"))
                .Concat(candidates);
            if (insight?.CanDeclareAssociatedType == true)
                candidates = candidates.Append(new("typename", "Keyword", "typename Name",
                    "Declare an associated type inline, for example `first?: typename First`.", SemanticRole: "keyword"));
        }
        Dictionary<string, SemanticMember> selected = new(StringComparer.Ordinal);
        foreach (SemanticMember candidate in candidates)
        {
            if (!candidate.Name.StartsWith(context.Qualifier, StringComparison.Ordinal)) continue;
            string remaining = candidate.Name[context.Qualifier.Length..];
            int separator = remaining.IndexOf("::", StringComparison.Ordinal);
            string name = separator < 0 ? remaining : remaining[..(separator + 2)];
            if (!name.StartsWith(context.Prefix, StringComparison.Ordinal)) continue;
            selected.TryAdd(name, separator < 0 ? candidate with { Name = name } : new(name, "Type",
                context.Qualifier + name, "A compile-time concept namespace. Select a named structural constraint with `::`.", SemanticRole: "type")
                { IsConstraint = true });
        }
        return selected.Values.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
    }
}
