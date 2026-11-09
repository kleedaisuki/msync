using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using PrettyPrompt.Rendering;

namespace Msync.Cli;

internal static partial class TerminalPresentation
{
    private static readonly object DiagnosticGate = new();
    internal const string HeadingColor = "1;36";
    internal const string CommandColor = "1;32";
    private const string GutterColor = "1;34";
    private const string ErrorColor = "1;31";
    private const string NoteColor = "1;36";

    public static string RequestedColor(string[] arguments)
    {
        int index = Array.LastIndexOf(arguments, "--color");
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : "auto";
    }

    public static bool Colors(string policy, bool error = false) =>
        Environment.GetEnvironmentVariable("NO_COLOR") is null && policy != "never" &&
        (policy == "always" || (!(error ? Console.IsErrorRedirected : Console.IsOutputRedirected)
            && Environment.GetEnvironmentVariable("TERM") != "dumb"));

    internal static string Paint(string text, string ansi, bool color) => color ? $"\u001b[{ansi}m{text}\u001b[0m" : text;

    internal static string Syntax(string source, bool color)
    {
        if (!color) return source;
        StringBuilder result = new();
        int position = 0;
        foreach (var token in PromptSyntax.Highlight(source))
        {
            result.Append(source.AsSpan(position, token.Start - position));
            result.Append(Paint(source[token.Start..token.End], token.Formatting.ForegroundCode!, color));
            position = token.End;
        }
        result.Append(source.AsSpan(position));
        return result.ToString();
    }

    public static void Cancelled(string policy, bool interactive = false)
    {
        Console.Error.WriteLine(Paint("cancelled", "1;33", Colors(policy, error: true)) + ": operation cancelled"
            + (interactive ? "; enter another statement or /quit" : ""));
    }

    public static void Error(Exception error, string policy, SourceSnapshot? source = null, string? help = null)
    {
        lock (DiagnosticGate) RenderError(error, policy, source, help);
    }

    private static void RenderError(Exception error, string policy, SourceSnapshot? source, string? help)
    {
        SourceDiagnostic? diagnostic = error switch { CompileException compiled => compiled.Diagnostic, SyntaxException syntax => syntax.Diagnostic, _ => null };
        Msync.Core.SourceSpan? runtimeSpan = (error as MsyncException)?.Span;
        if (diagnostic is null && runtimeSpan is { } span && source is { } snapshot && snapshot.Name == span.Source)
            diagnostic = new("E0300", error.Message, snapshot.Text, snapshot.Name, new(span.Start, span.Length),
                Label: "this runtime operation failed", Help: help);
        bool color = Colors(policy, error: true);
        if (diagnostic is not null) Render(diagnostic, color);
        else
        {
            Console.Error.WriteLine(Paint("error", ErrorColor, color) + $": {error.Message}");
            if (runtimeSpan is { } origin)
                Console.Error.WriteLine($"  {Paint("-->", GutterColor, color)} {origin.Source} (source offset {origin.Start}..{origin.Start + origin.Length})");
            if (help is not null) Detail("help", help, CommandColor, color);
        }
        string previous = error.Message;
        for (Exception? cause = error.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (cause.Message != previous)
            {
                string message = cause is System.ComponentModel.Win32Exception native
                    ? $"{cause.Message} (native error {native.NativeErrorCode})" : cause.Message;
                Detail("caused by", message, NoteColor, color);
            }
            previous = cause.Message;
        }
    }

    public static void Reference(string markdown, string policy)
    {
        if (!Colors(policy)) { Console.Write(markdown); return; }
        bool code = false;
        string[] lines = markdown.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string shown;
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                code = !code;
                shown = Paint(line, "90", color: true);
            }
            else shown = code ? Syntax(line, color: true)
                : line.StartsWith('#') ? Paint(line, HeadingColor, color: true)
                : InlineCode().Replace(line, match => Paint(match.Value, CommandColor, color: true));
            Console.Write(shown);
            if (index + 1 < lines.Length) Console.Write('\n');
        }
    }

    private static void Render(SourceDiagnostic diagnostic, bool color)
    {
        Console.Error.WriteLine(Paint($"error[{diagnostic.Code}]", ErrorColor, color) + $": {diagnostic.Message}");
        Excerpt(new(diagnostic.SourceName, diagnostic.Source, diagnostic.Span, diagnostic.Label ?? "here"), color, primary: true);
        foreach (var location in diagnostic.Related) Excerpt(location, color, primary: false);
        if (diagnostic.Note is { } note) Detail("note", note, NoteColor, color);
        if (diagnostic.Help is { } help) Detail("help", help, CommandColor, color);
        if (diagnostic.Fix is { } fix) Suggestion(diagnostic.Source, fix, color);
    }

    private static void Detail(string name, string text, string style, bool color)
    {
        string prefix = $"  {name}: ";
        int width = Math.Max(20, DisplayWidth() - prefix.Length);
        string[] paragraphs = text.Split('\n');
        bool first = true;
        foreach (string paragraph in paragraphs)
        {
            int start = 0;
            do
            {
                int length = Math.Min(width, paragraph.Length - start);
                if (start + length < paragraph.Length)
                {
                    int space = paragraph.LastIndexOf(' ', start + length, length);
                    if (space >= start) length = space - start;
                    else
                    {
                        int next = paragraph.IndexOf(' ', start + length);
                        length = (next < 0 ? paragraph.Length : next) - start;
                    }
                }
                string indent = first ? "  " + Paint(name, style, color) + ": " : new(' ', prefix.Length);
                Console.Error.WriteLine(indent + paragraph.Substring(start, length));
                first = false;
                start += length;
                while (start < paragraph.Length && paragraph[start] == ' ') start++;
            } while (start < paragraph.Length);
        }
    }

    private static void Excerpt(DiagnosticLocation location, bool color, bool primary)
    {
        SourceText source = new(location.Source);
        var first = source.Coordinates(location.Span.Start);
        var last = source.Coordinates(Math.Max(location.Span.Start, location.Span.End - 1));
        string name = Uri.TryCreate(location.SourceName, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : location.SourceName;
        Console.Error.WriteLine($"  {Paint(primary ? "-->" : "::: ", GutterColor, color)} {name}:{first.Line + 1}:{first.Column + 1}");
        int digits = (last.Line + 1).ToString(CultureInfo.InvariantCulture).Length;
        string padding = new(' ', digits);
        Console.Error.WriteLine($" {padding} {Paint("|", GutterColor, color)}");
        for (int line = first.Line; line <= last.Line; line++)
        {
            if (last.Line - first.Line > 5 && line == first.Line + 2)
            {
                Console.Error.WriteLine($" {padding} {Paint("...", GutterColor, color)}");
                line = last.Line - 1;
            }
            int start = source.Offset(line, 0);
            int end = LineEnd(source, line);
            int markedStart = Math.Max(start, location.Span.Start);
            int markedEnd = Math.Min(end, location.Span.End);
            string text = Expand(location.Source[start..end]);
            int before = Width(Expand(location.Source[start..markedStart]));
            int marked = Math.Max(1, Width(Expand(location.Source[start..markedEnd])) - before);
            var shown = Viewport(text, before, marked, Math.Max(24, DisplayWidth() - digits - 4));
            string gutter = (line + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits) + " |";
            Console.Error.WriteLine($" {Paint(gutter, GutterColor, color)} {Syntax(shown.Text, color)}");
            string label = line == last.Line ? " " + location.Message : "";
            string marker = new(primary ? '^' : '-', shown.Marked);
            Console.Error.WriteLine($" {padding} {Paint("|", GutterColor, color)} {new string(' ', shown.Before)}{Paint(marker + label, primary ? ErrorColor : NoteColor, color)}");
        }
    }

    private static void Suggestion(string source, DiagnosticFix fix, bool color)
    {
        Detail("suggestion", fix.Title, CommandColor, color);
        SourceText text = new(source);
        int line = text.Coordinates(fix.Span.Start).Line;
        int start = text.Offset(line, 0);
        int end = LineEnd(text, text.Coordinates(fix.Span.End).Line);
        string replacement = source[start..fix.Span.Start] + fix.Replacement + source[fix.Span.End..end];
        foreach (string row in replacement.Split('\n'))
            Console.Error.WriteLine("    " + Paint("+ ", CommandColor, color) + Syntax(Expand(row.TrimEnd('\r')), color));
    }

    private static (string Text, int Before, int Marked) Viewport(string text, int before, int marked, int width)
    {
        if (Width(text) <= width) return (text, before, marked);
        int left = Math.Max(0, before - width / 3);
        int right = left + width - (left == 0 ? 3 : 6);
        StringBuilder result = new();
        if (left > 0) result.Append("...");
        int column = 0, skipped = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int cells = Width(element);
            if (column + cells <= left) skipped = column + cells;
            else if (column < right) result.Append(element);
            column += cells;
            if (column >= right) break;
        }
        bool tail = Width(text) > column;
        if (tail) result.Append("...");
        int visibleBefore = before - skipped + (left == 0 ? 0 : 3);
        int visibleMarked = Math.Max(1, Math.Min(marked, column - Math.Max(before, skipped)));
        return (result.ToString(), visibleBefore, visibleMarked);
    }

    private static int LineEnd(SourceText source, int line)
    {
        int start = source.Offset(line, 0);
        int end = source.Offset(line + 1, 0);
        while (end > start && source.Text[end - 1] is '\r' or '\n') end--;
        return end;
    }

    private static int DisplayWidth() => !Console.IsErrorRedirected && Console.WindowWidth > 0 ? Math.Min(100, Console.WindowWidth) : 100;

    private static string Expand(string text)
    {
        if (!text.Contains('\t', StringComparison.Ordinal)) return text;
        StringBuilder result = new();
        int start = 0;
        foreach (int index in Enumerable.Range(0, text.Length).Where(i => text[i] == '\t'))
        {
            result.Append(text.AsSpan(start, index - start));
            result.Append(' ', 4 - Width(result.ToString()) % 4);
            start = index + 1;
        }
        result.Append(text.AsSpan(start));
        return result.ToString();
    }

    private static int Width(string text) => UnicodeWidth.GetWidth(text.AsSpan());

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();

}
