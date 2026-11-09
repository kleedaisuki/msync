namespace Msync.Compiler.Syntax;

public static class TypeSyntaxFacts
{
    internal static IEnumerable<TypeSyntax> Annotations(Node node) => node switch
    {
        LetStatement { Type: { } type } => [type],
        FunctionExpr function => function.Parameters.Select(parameter => parameter.Type).Append(function.ReturnType).OfType<TypeSyntax>(),
        NativeDeclaration native => native.Parameters.Select(parameter => parameter.Type).Append(native.ReturnType).OfType<TypeSyntax>(),
        ConceptDeclaration concept => concept.Bases,
        ConceptMember requirement => new[] { requirement.Type, requirement.ReturnType }
            .Concat(requirement.Parameters?.Select(parameter => parameter.Type) ?? []).OfType<TypeSyntax>(),
        TypeExpr value => [value.Type],
        CastExpr cast => [cast.Type],
        TypePattern pattern => [pattern.Type],
        _ => []
    };

    public static bool IsReturnTypeArrow(string source)
    {
        IReadOnlyList<Token> tokens = new Lexer(source, "<editor>").LexForEditor(out _);
        int end = tokens.Count - 2;
        if (end < 1 || tokens[end].Kind != TokenKind.Arrow) return false;
        int before = end - 1;
        if (tokens[before].Kind == TokenKind.RightParen)
        {
            int depth = 1;
            while (before-- > 0 && depth != 0)
            {
                if (tokens[before].Kind == TokenKind.RightParen) depth++;
                if (tokens[before].Kind == TokenKind.LeftParen) depth--;
            }
            if (depth != 0) return false;
        }
        return IsFn(tokens, before) || before > 0 && tokens[before].Kind == TokenKind.Atom && IsFn(tokens, before - 1);
    }

    private static bool IsFn(IReadOnlyList<Token> tokens, int index) => index >= 0
        && tokens[index].Kind == TokenKind.Atom && tokens[index].Text == "fn";
}
