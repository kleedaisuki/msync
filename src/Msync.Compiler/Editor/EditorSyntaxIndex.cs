using Msync.Compiler.Syntax;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed record EditorSyntaxIndex(
    IReadOnlyList<SemanticOccurrence> Occurrences,
    IReadOnlyList<EditorSymbol> Symbols,
    IReadOnlyList<SourceSpan> TypeSpans,
    IReadOnlyList<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> AssociatedTypes,
    IReadOnlyList<(SourceSpan Span, EditorLocation Target)> AssociatedDefinitions)
{
    public static EditorSyntaxIndex Create(Document document, string sourceName, string source)
    {
        Dictionary<SourceSpan, SemanticOccurrence> occurrences = [];
        List<SourceSpan> typeSpans = [];
        Seed(document.Statements, source, occurrences, typeSpans);
        return new(occurrences.Values.ToArray(), EditorSymbols.Collect(document.Statements), typeSpans,
            ConceptTypeScopes(document.Statements).ToArray(),
            AssociatedTypeDefinitions(document.Statements, sourceName, source).ToArray());
    }

    private static string Slice(string source, SourceSpan span) => source[span.Start..span.End];

    private static string FunctionSignature(string? name, IReadOnlyList<Parameter> parameters,
        string result, string source) =>
        $"fn {name ?? ""}({string.Join(", ", parameters.Select(parameter =>
            $"{parameter.Name}{(parameter.Nullable ? "?" : "")}: {(parameter.Type is null ? "inferred at call" : Slice(source, parameter.Type.Span))}"))}) -> {result}";

    private static IEnumerable<(SourceSpan Span, EditorLocation Target)> AssociatedTypeDefinitions(IEnumerable<Node> nodes,
        string sourceName, string source)
    {
        foreach (Node node in nodes)
        {
            if (node is ConceptDeclaration concept)
            {
                var declarations = concept.Types.Where(type => type.NameSpan is { Length: > 0 }).DistinctBy(type => type.Name).ToDictionary(type => type.Name,
                    type => new EditorLocation(sourceName, source, type.NameSpan!.Value), StringComparer.Ordinal);
                foreach (TypeSyntax type in RequirementTypes(concept.Members))
                    if (declarations.TryGetValue(type.Name, out var target))
                        yield return (EditorTypeNames.Parts(source, type).Last().Span, target);
            }
            foreach (var reference in AssociatedTypeDefinitions(SyntaxTraversal.Children(node), sourceName, source)) yield return reference;
        }

        static IEnumerable<TypeSyntax> RequirementTypes(IEnumerable<Node> requirements)
        {
            foreach (Node node in requirements)
            {
                foreach (TypeSyntax type in TypeSyntaxFacts.Annotations(node))
                    foreach (TypeSyntax nested in TypeTree(type)) yield return nested;
            }
        }

        static IEnumerable<TypeSyntax> TypeTree(TypeSyntax type)
        {
            yield return type;
            foreach (TypeSyntax child in type.Arguments.Concat(type.Alternatives))
                foreach (TypeSyntax nested in TypeTree(child)) yield return nested;
        }
    }

    private static IEnumerable<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> ConceptTypeScopes(IEnumerable<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            if (node is ConceptDeclaration concept)
                yield return (concept.Span, concept.Types.Where(type => type.NameSpan is { Length: > 0 })
                    .Select(type => new SemanticMember(type.Name, "Type", $"typename {type.Name}",
                    "An associated type selected from the constrained Scope.", SemanticRole: "type")
                    { IsConstraint = true }).ToArray());
            foreach (var scope in ConceptTypeScopes(SyntaxTraversal.Children(node))) yield return scope;
        }
    }

    private static void Seed(IEnumerable<Node> nodes, string source, Dictionary<SourceSpan, SemanticOccurrence> occurrences, List<SourceSpan> typeSpans)
    {
        foreach (Node node in nodes)
        {
            if (node is FunctionExpr functionType)
            {
                foreach (Parameter parameter in functionType.Parameters) AddType(parameter.Type);
                AddType(functionType.ReturnType);
            }
            if (node is LetStatement letType) AddType(letType.Type);
            if (node is NativeDeclaration nativeType)
            {
                foreach (Parameter parameter in nativeType.Parameters) AddType(parameter.Type);
                AddType(nativeType.ReturnType);
            }
            if (node is TypeExpr valueType) AddType(valueType.Type);
            if (node is CastExpr cast) AddType(cast.Type);
            if (node is ConceptDeclaration conceptType)
                foreach (TypeSyntax inherited in conceptType.Bases) AddType(inherited);
            if (node is ConceptMember requirement)
            {
                AddType(requirement.Type);
                AddType(requirement.ReturnType);
                if (requirement.Parameters is { } parameters)
                    foreach (Parameter parameter in parameters) AddType(parameter.Type);
            }
            switch (node)
            {
                case FunctionDeclaration function:
                    Add(function.Function.NameSpan, "Function", FunctionSignature(function.Name, function.Function.Parameters,
                        function.Function.ReturnType is { } result ? Slice(source, result.Span) : "inferred at call", source));
                    break;
                case NativeDeclaration native:
                    Add(native.NameSpan, "Function", FunctionSignature(native.Name, native.Parameters, Slice(source, native.ReturnType.Span), source));
                    break;
                case ScopeDeclaration scope: Add(scope.NameSpan, "Scope", $"{scope.Name}: Scope", "type"); break;
                case ScopeExpr { Name: { } name } scope: Add(scope.NameSpan, "Scope", $"scope {name}", "type"); break;
                case ConceptDeclaration concept:
                    Add(concept.NameSpan, "Concept", $"concept {concept.Name}", "type");
                    if (concept.NameSpan is { } conceptName)
                        occurrences[conceptName] = occurrences[conceptName] with
                        {
                            Description = "A named structural constraint. Required fields and callable signatures are checked at specialization."
                                + (concept.Members.Count == 0 ? "" : "\n\nRequired members:\n"
                                    + string.Join("\n", concept.Members.Select(member => $"- `{Slice(source, member.Span)}`")))
                        };
                    Seed(concept.Types, source, occurrences, typeSpans);
                    Seed(concept.Members, source, occurrences, typeSpans);
                    break;
                case ConceptAssociatedType associated:
                    Add(associated.NameSpan, "Type", $"typename {associated.Name}", "type");
                    break;
                case ConceptMember { Parameters: { } parameters } member:
                    Add(member.NameSpan, "Function", FunctionSignature(member.Name, parameters,
                        member.ReturnType is { } expected ? Slice(source, expected.Span) : "inferred at call", source));
                    break;
                case ConceptMember member:
                    Add(member.NameSpan, member.Type is { } required ? Slice(source, required.Span) : "unknown",
                        Slice(source, member.Span));
                    break;
                case FunctionExpr { Name: { } name } function:
                    Add(function.NameSpan, "Function", FunctionSignature(name, function.Parameters,
                        function.ReturnType is { } namedResult ? Slice(source, namedResult.Span) : "inferred at call", source));
                    break;
                case LetStatement { Pattern: NamePattern name, Value: FunctionExpr function }:
                    Add(name.Span, "Function", FunctionSignature(name.Name, function.Parameters,
                        function.ReturnType is { } lambdaResult ? Slice(source, lambdaResult.Span) : "inferred at call", source));
                    break;
                case LetStatement { Pattern: NamePattern name, Value: ScopeExpr }: Add(name.Span, "Scope", $"{name.Name}: Scope"); break;
                case ScopeMember { Value: FunctionExpr function } member:
                    Add(member.NameSpan, "Function", FunctionSignature(member.Name, function.Parameters,
                        function.ReturnType is { } memberResult ? Slice(source, memberResult.Span) : "inferred at call", source));
                    break;
                case ScopeMember { Value: ScopeExpr } member: Add(member.NameSpan, "Scope", $"{member.Name}: Scope"); break;
            }
            Seed(SyntaxTraversal.Children(node), source, occurrences, typeSpans);
        }
        void AddType(TypeSyntax? type)
        {
            if (type is null) return;
            int start = type.Span.Start;
            while (start > 0 && char.IsWhiteSpace(source[start - 1])) start--;
            typeSpans.Add(new(start, type.Span.End - start));
            string prefix = "";
            foreach (var (part, name) in EditorTypeNames.Parts(source, type))
            {
                prefix += prefix.Length == 0 ? part : "::" + part;
                occurrences[name] = new(name, "Type", type.DeclaresAssociatedType ? $"typename {type.Name}" : prefix,
                    type.DeclaresAssociatedType ? "An associated type declared inline and inferred from the constrained value."
                        : prefix == type.Name ? "A type or structural concept constraint. It is checked without executing the expression."
                        : "A compile-time standard-library namespace. Select a concept with `::`.", SemanticRole: "type");
            }
            foreach (TypeSyntax argument in type.Arguments) AddType(argument);
            foreach (TypeSyntax alternative in type.Alternatives) AddType(alternative);
        }
        void Add(SourceSpan? span, string type, string signature, string? role = null)
        {
            if (span is { } location) occurrences[location] = new(location, type, signature,
                type == "Concept" ? "A named structural constraint. Required fields and function signatures are checked at specialization."
                    : type == "Type" ? "An associated type selected from the constrained Scope."
                    : type == "Function" ? "A callable value. Its generic body is checked when specialized by a call."
                    : "A lexical Scope. Select its declared members with `::`.",
                SemanticRole: role ?? (type == "Function" ? "function" : "variable"));
        }
    }
}
