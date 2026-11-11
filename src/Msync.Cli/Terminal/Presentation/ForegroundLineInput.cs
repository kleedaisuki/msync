using System.Globalization;
using System.Text;
using PrettyPrompt.Rendering;

namespace Msync.Cli;

internal enum ForegroundLineEnd { Enter, EndOfInput, Dismissed }
internal enum ForegroundLineMode { Text, Credential }
internal readonly record struct ForegroundLine(string Text, ForegroundLineEnd End);

internal static class ForegroundLineInput
{
    private const string TabEcho = "    ";

    public static ForegroundLine Read(TextWriter? echo, ForegroundLineMode mode, CancellationToken token)
    {
        StringBuilder line = new();
        using CancellableConsoleInput input = new(token);
        while (true)
        {
            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Enter) return new(line.ToString(), ForegroundLineEnd.Enter);
            if (key.KeyChar == '\u0003' || mode == ForegroundLineMode.Credential && key.Key == ConsoleKey.Escape)
                return new("", ForegroundLineEnd.Dismissed);
            if (key.KeyChar is '\u0004' or '\u001a') return new(line.ToString(), ForegroundLineEnd.EndOfInput);
            if (key.Key == ConsoleKey.Backspace)
            {
                RemoveLastElement(line, echo);
                continue;
            }
            if (key.KeyChar == '\u0015')
            {
                while (line.Length != 0) RemoveLastElement(line, echo);
                continue;
            }
            if (key.KeyChar == '\t' && mode == ForegroundLineMode.Text)
            {
                line.Append('\t');
                echo?.Write(TabEcho);
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            line.Append(key.KeyChar);
            echo?.Write(key.KeyChar);
        }
    }

    private static void RemoveLastElement(StringBuilder text, TextWriter? echo)
    {
        if (text.Length == 0) return;
        string value = text.ToString();
        int start = StringInfo.ParseCombiningCharacters(value)[^1];
        if (echo is not null)
        {
            int cells = value[start] == '\t' ? TabEcho.Length : UnicodeWidth.GetWidth(value.AsSpan(start));
            while (cells-- > 0) echo.Write("\b \b");
        }
        text.Length = start;
    }
}
