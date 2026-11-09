using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private static (int Line, int Column) Coordinates(string text, int offset)
    {
        int line = 0, start = 0;
        for (int i = 0; i < offset; i++) if (text[i] == '\n') { line++; start = i + 1; }
        return (line, offset - start);
    }

    private static JsonObject Range(string text, int offset, int length)
    {
        var start = Coordinates(text, offset);
        var end = Coordinates(text, offset + length);
        return new JsonObject { ["start"] = Position(start.Line, start.Column), ["end"] = Position(end.Line, end.Column) };
    }

    private static JsonObject Position(int line, int column) => new() { ["line"] = line, ["character"] = column };
    private static string Word(string text, int offset)
    {
        int start = offset, end = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        return text[start..end];
    }
}
