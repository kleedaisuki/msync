using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public enum EditorLexemeKind { Atom, Number, String, Regex, Comment, Operator, Delimiter }

public readonly record struct EditorLexeme(EditorLexemeKind Kind, SourceSpan Span, bool Complete = true);

public static class EditorLexemes
{
    public static IReadOnlyList<EditorLexeme> Scan(string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<EditorLexeme> result = [];
        new Lexer(source, "<editor>", cancellationToken, result.Add).LexForEditor(out _);
        return result.ToArray();
    }
}
