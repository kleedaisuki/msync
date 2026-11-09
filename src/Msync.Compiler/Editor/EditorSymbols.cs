using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

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
                    symbols.AddRange(Collect(SyntaxTraversal.Children(node)));
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
        _ => Collect(SyntaxTraversal.Children(value))
    };
    private static IEnumerable<NamePattern> Names(Pattern pattern)
    {
        if (pattern is NamePattern { Name: not "_" } name) yield return name;
        if (pattern is TuplePattern tuple)
            foreach (Pattern item in tuple.Items)
                foreach (NamePattern nested in Names(item)) yield return nested;
    }
}
