namespace Msync.Compiler.Syntax;

public static class NameSyntax
{
    public static SourceSpan SpanAt(string source, int offset)
    {
        int start = offset, end = offset;
        while (start > 0 && !Boundary(source[start - 1])) start--;
        while (end < source.Length && !Boundary(source[end])) end++;
        if ((start == 0 || source[start - 1] != '/') && start + 1 < end && source[start] == '-' && source[start + 1] != '-') start++;
        return new(start, end - start);
    }

    public static bool Boundary(char character) => char.IsWhiteSpace(character)
        || "{}()[],;?:=|<>!\"$/@+*%".Contains(character, StringComparison.Ordinal);
}
