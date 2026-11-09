using Msync.Compiler.Syntax;

namespace Msync.Cli;

internal sealed class SubmissionSyntax
{
    private string? text;
    private Document? document;

    public bool IsIncomplete(string source)
    {
        text = source;
        try { document = Parser.Parse(source, "<repl>"); return false; }
        catch (SyntaxException error) { document = null; return error.IsIncomplete; }
    }

    public Document? Take(string source)
    {
        Document? result = string.Equals(text, source, StringComparison.Ordinal) ? document : null;
        text = null;
        document = null;
        return result;
    }
}
