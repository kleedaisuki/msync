using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

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
