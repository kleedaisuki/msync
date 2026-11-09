using Msync.Compiler.Syntax;
using System.Runtime.CompilerServices;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public sealed record EditorLocation(string SourceName, string Source, SourceSpan Span);

internal static class EditorTypeNames
{
    public static IEnumerable<(string Name, SourceSpan Span)> Parts(string source, TypeSyntax type)
    {
        int cursor = type.Span.Start + (type.DeclaresAssociatedType ? "typename".Length : 0);
        foreach (string part in type.Name.Split("::", StringSplitOptions.None))
        {
            string text = part.TrimStart('$');
            int start = source.IndexOf(text, cursor, StringComparison.Ordinal);
            SourceSpan span = new(start, text.Length);
            yield return (part, span);
            cursor = span.End;
        }
    }
}

public enum EditorSymbolKind { Variable, Function, Scope, Field, Parameter }
public sealed record EditorSymbol(string Name, SourceSpan Span, SourceSpan SelectionSpan, EditorSymbolKind Kind,
    IReadOnlyList<EditorSymbol> Children);

internal static class EditorSymbols
{
    public static IReadOnlyList<EditorSymbol> Collect(IEnumerable<Node> nodes)
    {
        List<EditorSymbol> symbols = [];
        foreach (Node node in nodes)
        {
            switch (node)
            {
                case FunctionDeclaration function:
                    Add(function.Name, function.Function.NameSpan, function.Span, EditorSymbolKind.Function, FunctionChildren(function.Function));
                    break;
                case NativeDeclaration native:
                    Add(native.Name, native.NameSpan, native.Span, EditorSymbolKind.Function, Parameters(native.Parameters));
                    break;
                case ScopeDeclaration scope:
                    Add(scope.Name, scope.NameSpan, scope.Span, EditorSymbolKind.Scope, Collect(scope.Scope.Members));
                    break;
                case ScopeExpr { Name: { } name } scope:
                    Add(name, scope.NameSpan, scope.Span, EditorSymbolKind.Scope, Collect(scope.Members));
                    break;
                case ConceptDeclaration concept:
                    Add(concept.Name, concept.NameSpan, concept.Span, EditorSymbolKind.Scope,
                        Collect(concept.Types.Cast<Node>().Concat(concept.Members)));
                    break;
                case ConceptAssociatedType associated:
                    Add(associated.Name, associated.NameSpan, associated.Span, EditorSymbolKind.Scope, []);
                    break;
                case ConceptMember member:
                    Add(member.Name, member.NameSpan, member.Span,
                        member.Parameters is null ? EditorSymbolKind.Field : EditorSymbolKind.Function,
                        member.Parameters is { } parameters ? Parameters(parameters) : []);
                    break;
                case LetStatement let:
                    foreach (NamePattern name in Names(let.Pattern))
                        Add(name.Name, name.Span, let.Span, ValueKind(let.Value), CollectValue(let.Value));
                    break;
                case ScopeMember member:
                    Add(member.Name, member.NameSpan, member.Span,
                        member.Value is FunctionExpr ? EditorSymbolKind.Function : EditorSymbolKind.Field, CollectValue(member.Value));
                    break;
                case ForExpr loop:
                    foreach (NamePattern name in Names(loop.Pattern))
                        Add(name.Name, name.Span, loop.Span, EditorSymbolKind.Variable, []);
                    symbols.AddRange(Collect(loop.Body.Statements));
                    break;
                default:
                    symbols.AddRange(Collect(Compilation.Children(node)));
                    break;
            }
        }
        return symbols;

        void Add(string name, SourceSpan? selection, SourceSpan span, EditorSymbolKind kind, IReadOnlyList<EditorSymbol> children)
        {
            if (name.Length != 0 && selection is { Length: > 0 } selected)
                symbols.Add(new(name, span, selected, kind, children));
            else symbols.AddRange(children);
        }
    }

    private static EditorSymbolKind ValueKind(Expr value) => value is FunctionExpr ? EditorSymbolKind.Function : EditorSymbolKind.Variable;
    private static IReadOnlyList<EditorSymbol> FunctionChildren(FunctionExpr function) =>
        Parameters(function.Parameters).Concat(Collect(function.Body.Statements)).ToArray();
    private static IReadOnlyList<EditorSymbol> Parameters(IReadOnlyList<Parameter> parameters) => parameters
        .Where(parameter => parameter.Name.Length != 0)
        .Select(parameter => new EditorSymbol(parameter.Name, parameter.Span, new(parameter.Span.Start, parameter.Name.Length), EditorSymbolKind.Parameter, []))
        .ToArray();
    private static IReadOnlyList<EditorSymbol> CollectValue(Expr value) => value switch
    {
        FunctionExpr function => FunctionChildren(function),
        ScopeExpr { Name: not null } scope => Collect([scope]),
        ScopeExpr scope => Collect(scope.Members),
        _ => Collect(Compilation.Children(value))
    };
    private static IEnumerable<NamePattern> Names(Pattern pattern)
    {
        if (pattern is NamePattern { Name: not "_" } name) yield return name;
        if (pattern is TuplePattern tuple)
            foreach (Pattern item in tuple.Items)
                foreach (NamePattern nested in Names(item)) yield return nested;
    }
}

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
