using System.Globalization;
using System.Text;
using PrettyPrompt.Rendering;

namespace Msync.Cli;

internal static class SshTerminalPrompt
{
    public static Task<string?> ReadAsync(SshPrompt request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected) throw new IOException("Native SSH authentication needs an interactive terminal or a configured SSH askpass handler.");
        Console.Error.WriteLine($"SSH {request.Endpoint}");
        Console.Error.Write(request.Prompt);
        if (request.Kind == "notice") { Console.Error.WriteLine(); return Task.FromResult<string?>(""); }
        StringBuilder answer = new();
        bool echo = request.Kind != "secret";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return Task.FromResult<string?>(answer.ToString()); }
            if (key.Key == ConsoleKey.Escape || key.KeyChar is '\u0003' or '\u0004' or '\u001a')
            { Console.Error.WriteLine(); return Task.FromResult<string?>(null); }
            if (key.Key == ConsoleKey.Backspace)
            {
                RemoveLastElement(answer, echo);
                continue;
            }
            if (key.KeyChar == '\u0015')
            {
                while (answer.Length != 0) RemoveLastElement(answer, echo);
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            answer.Append(key.KeyChar);
            if (echo) Console.Error.Write(key.KeyChar);
        }
    }

    private static void RemoveLastElement(StringBuilder text, bool echo)
    {
        if (text.Length == 0) return;
        string value = text.ToString();
        int[] elements = StringInfo.ParseCombiningCharacters(value);
        int start = elements[^1];
        if (echo)
            for (int cells = UnicodeWidth.GetWidth(value.AsSpan(start)); cells > 0; cells--) Console.Error.Write("\b \b");
        text.Length = start;
    }
}
