using System.Collections;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public readonly record struct EditorTokenRole(SourceSpan Span, string Role);

// Keep semantic presentation on demand. Diagnostics have no reason to format
// every signature, and highlighting needs roles rather than member trees.
internal sealed class EditorOccurrences : IReadOnlyList<SemanticOccurrence>
{
    private readonly EditorSyntaxIndex syntax;
    private readonly EditorSemanticIndex contexts;
    private readonly Lazy<IReadOnlyList<SemanticOccurrence>> all;

    internal EditorOccurrences(EditorSyntaxIndex syntax, EditorSemanticIndex contexts)
    {
        this.syntax = syntax;
        this.contexts = contexts;
        all = new(() => Project());
    }

    public int Count => all.Value.Count;
    public SemanticOccurrence this[int index] => all.Value[index];
    public IEnumerator<SemanticOccurrence> GetEnumerator() => all.Value.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal SemanticOccurrence? At(int offset) => Project(span => span.Start <= offset && offset < span.End)
        .MinBy(item => item.Span.Length);

    internal IReadOnlyList<EditorTokenRole> TokenRoles()
    {
        Dictionary<SourceSpan, string> roles = [];
        foreach (var item in syntax.Occurrences)
            if (item.SemanticRole is "function" or "type" || item.Type is "Scope" or "Scope?")
                roles[item.Span] = item.SemanticRole;
        foreach (var (span, role) in contexts.TokenRoles())
            if (role is null) roles.Remove(span);
            else roles[span] = role;
        return roles.Select(pair => new EditorTokenRole(pair.Key, pair.Value)).ToArray();
    }

    private IReadOnlyList<SemanticOccurrence> Project(Func<SourceSpan, bool>? select = null)
    {
        var occurrences = syntax.Occurrences.Where(item => select is null || select(item.Span))
            .ToDictionary(item => item.Span);
        foreach (var occurrence in contexts.Occurrences(select)) occurrences[occurrence.Span] = occurrence;
        foreach (var (span, occurrence) in occurrences.ToArray())
        {
            if (occurrence.Type != "Type" || occurrence.SemanticRole != "type"
                || LanguageTypes.TryResolve(occurrence.Signature, out _)) continue;
            var concept = contexts.ConceptsAt(span.Start).Concat(EditorCompiler.StandardConceptBindings)
                .FirstOrDefault(item => item.Name == occurrence.Signature || item.Signature == occurrence.Signature);
            if (concept is not null) occurrences[span] = occurrence with
            { Signature = concept.Signature, Description = concept.Description, Members = concept.Members };
        }
        return occurrences.Values.OrderBy(item => item.Span.Start).ThenBy(item => item.Span.Length).ToArray();
    }
}
