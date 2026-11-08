using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public sealed record DiagnosticLocation(string SourceName, string Source, SourceSpan Span, string Message);
public sealed record DiagnosticFix(string Title, SourceSpan Span, string Replacement);
public sealed record SourceDiagnostic(string Code, string Message, string Source, string SourceName, SourceSpan Span,
    string? Label = null, string? Help = null, string? Note = null, DiagnosticFix? Fix = null)
{
    public List<DiagnosticLocation> Related { get; } = [];
    public (int Line, int Column) Position => Locate(Source, Span.Start);
    public string EditorMessage => Message + (Note is null ? "" : $"\n\n{Note}") + (Help is null ? "" : $"\n\nHelp: {Help}");

    public static (int Line, int Column) Locate(string source, int offset)
    {
        int line = 1, start = 0;
        for (int i = 0; i < Math.Min(offset, source.Length); i++)
        {
            if (source[i] is not ('\r' or '\n')) continue;
            if (source[i] == '\r' && i + 1 < offset && source[i + 1] == '\n') i++;
            line++; start = i + 1;
        }
        return (line, Math.Min(offset, source.Length) - start + 1);
    }
}

public sealed class CompileException : Exception
{
    public SourceDiagnostic Diagnostic { get; }
    public string SourceName => Diagnostic.SourceName;
    public SourceSpan Span => Diagnostic.Span;
    public int Line => Diagnostic.Position.Line;
    public int Column => Diagnostic.Position.Column;

    public CompileException(string message, string source, string sourceName, SourceSpan span,
        string? label = null, string? help = null, string? note = null, DiagnosticFix? fix = null, string code = "E0200")
        : base($"{sourceName}:{SourceDiagnostic.Locate(source, span.Start).Line}:{SourceDiagnostic.Locate(source, span.Start).Column}: {message}")
    {
        Diagnostic = new(code, message, source, sourceName, span, label, help, note, fix);
    }
}
