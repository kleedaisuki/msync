using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed class SourceText(string text)
{
    private readonly int[] lines = LineStarts(text);
    public string Text { get; } = text;

    public int Offset(int line, int column)
    {
        if (line >= lines.Length) return Text.Length;
        int start = lines[Math.Max(0, line)];
        int end = line + 1 < lines.Length ? lines[line + 1] : Text.Length;
        while (end > start && Text[end - 1] is '\r' or '\n') end--;
        return Math.Min(end, start + Math.Max(0, column));
    }

    public (int Line, int Column) Coordinates(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int line = Array.BinarySearch(lines, offset);
        if (line < 0) line = ~line - 1;
        return (line, offset - lines[line]);
    }

    public JsonObject Range(int offset, int length)
    {
        var start = Coordinates(offset);
        var end = Coordinates(offset + length);
        return new() { ["start"] = Position(start.Line, start.Column), ["end"] = Position(end.Line, end.Column) };
    }

    private static JsonObject Position(int line, int column) => new() { ["line"] = line, ["character"] = column };

    private static int[] LineStarts(string text)
    {
        List<int> result = [0];
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            result.Add(index + 1);
        }
        return result.ToArray();
    }
}
