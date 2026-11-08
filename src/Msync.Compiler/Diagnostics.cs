using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public sealed class CompileException : Exception
{
    public string SourceName { get; }
    public SourceSpan Span { get; }
    public int Line { get; }
    public int Column { get; }

    public CompileException(string message, string source, string sourceName, SourceSpan span)
        : base(Format(message, source, sourceName, span.Start))
    {
        SourceName = sourceName;
        Span = span;
        (Line, Column) = Locate(source, span.Start);
    }

    private static string Format(string message, string source, string sourceName, int offset)
    {
        var (line, column) = Locate(source, offset);
        return $"{sourceName}:{line}:{column}: {message}";
    }

    private static (int Line, int Column) Locate(string source, int offset)
    {
        int line = 1, column = 1;
        foreach (var c in source.AsSpan(0, Math.Min(offset, source.Length)))
        {
            if (c == '\n') { line++; column = 1; }
            else column++;
        }
        return (line, column);
    }
}
